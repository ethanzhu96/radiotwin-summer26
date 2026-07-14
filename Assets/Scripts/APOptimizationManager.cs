using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class APOptimizationManager : MonoBehaviour
{
    [Header("Evaluation")]
    [SerializeField] private RtFloorField floorField;
    [SerializeField] private float coverageThresholdDbm = -70f;
    [SerializeField, Min(0f)] private float coverageWeight = 0.50f;
    [SerializeField, Min(0f)] private float tenthPercentileWeight = 0.30f;
    [SerializeField, Min(0f)] private float uniformityWeight = 0.20f;
    [SerializeField, Min(1)] private int cellsPerFrame = 4;

    private APPlacementManager placement;
    private bool isEvaluating;
    private int evaluationGeneration;

    public void Initialize(APPlacementManager manager) { placement = manager; }

    private void Start()
    {
        if (placement == null) placement = GetComponent<APPlacementManager>();
        if (floorField == null) floorField = FindFirstObjectByType<RtFloorField>();
    }

    public void RequestCandidateEvaluation()
    {
        Debug.Log("[APOptimization] Evaluation requested.");
        if (isEvaluating || placement == null || placement.State == APPlacementState.Evaluating)
        { Reject("Candidate evaluation is already running."); return; }
        if (placement.State != APPlacementState.EditingCandidates)
        { Reject("Start a new candidate round before evaluating again."); return; }
        if (placement.IsLogging)
        { Reject("Stop trajectory logging before evaluating router candidates."); return; }
        if (placement.Candidates.Count < placement.MinimumCandidates)
        { Reject("Place at least " + placement.MinimumCandidates + " router candidates."); return; }
        if (placement.MatchedRoomTransform == null)
        { Reject("Matched room is not ready."); return; }
        if (floorField == null) floorField = FindFirstObjectByType<RtFloorField>();
        if (floorField == null || !floorField.IsReadyForCandidateEvaluation)
        { Reject("RT evaluation pipeline is not ready."); return; }
        StartCoroutine(EvaluateCandidatesRoutine(++evaluationGeneration));
    }

    private IEnumerator EvaluateCandidatesRoutine(int generation)
    {
        isEvaluating = true;
        placement.State = APPlacementState.Evaluating;
        List<Vector2Int> grid = floorField.GetEvaluationGridIndices();
        Debug.Log("[APOptimization] Evaluating " + placement.Candidates.Count +
            " candidates over " + grid.Count + " identical receiver cells.");

        for (int candidateIndex = 0; candidateIndex < placement.Candidates.Count; candidateIndex++)
        {
            if (generation != evaluationGeneration) yield break;
            APCandidateData candidate = placement.Candidates[candidateIndex];
            candidate.status = APCandidateStatus.Evaluating;
            placement.Markers[candidateIndex].SetStatus(APCandidateStatus.Evaluating);
            Vector3 txWorld = placement.MatchedRoomTransform.TransformPoint(candidate.roomLocalPosition);
            List<RtFloorField.RtCell> result = new List<RtFloorField.RtCell>(grid.Count);
            for (int cellIndex = 0; cellIndex < grid.Count; cellIndex++)
            {
                RtFloorField.RtCell cell = floorField.EvaluateCell(txWorld, grid[cellIndex]);
                if (cell != null) result.Add(cell);
                if ((cellIndex + 1) % Mathf.Max(1, cellsPerFrame) == 0) yield return null;
            }
            candidate.heatmapCells = result;
            if (!CalculateMetrics(candidate, result))
            { Fail("Candidate " + candidate.candidateId + " produced no valid RT cells."); yield break; }
            candidate.status = APCandidateStatus.Evaluated;
            Debug.Log("[APOptimization] " + candidate.candidateId + " score=" + candidate.overallScore.ToString("F1") +
                " coverage=" + candidate.coveragePercent.ToString("F1") + "% mean=" +
                candidate.meanRssiDbm.ToString("F1") + " p10=" + candidate.tenthPercentileRssiDbm.ToString("F1") + ".");
        }

        ApplyRanking();
        isEvaluating = false;
        placement.State = APPlacementState.ShowingResults;
    }

    private bool CalculateMetrics(APCandidateData candidate, IReadOnlyList<RtFloorField.RtCell> cells)
    {
        List<float> values = new List<float>(cells.Count);
        double sum = 0;
        int covered = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            float value = cells[i].predictedRssiDb;
            if (float.IsNaN(value) || float.IsInfinity(value)) continue;
            values.Add(value); sum += value; if (value >= coverageThresholdDbm) covered++;
        }
        if (values.Count == 0) return false;
        values.Sort();
        float mean = (float)(sum / values.Count);
        int p10Index = Mathf.Clamp(Mathf.CeilToInt(values.Count * .1f) - 1, 0, values.Count - 1);
        double variance = 0;
        for (int i = 0; i < values.Count; i++) { double delta = values[i] - mean; variance += delta * delta; }
        float standardDeviation = Mathf.Sqrt((float)(variance / values.Count));
        float coverageNormalized = covered / (float)values.Count;
        float p10Normalized = Mathf.InverseLerp(floorField.WeakRssiDb, floorField.StrongRssiDb, values[p10Index]);
        float uniformity = Mathf.Clamp01(1f - standardDeviation / 40f);
        float weightTotal = Mathf.Max(.0001f, coverageWeight + tenthPercentileWeight + uniformityWeight);
        candidate.coveragePercent = coverageNormalized * 100f;
        candidate.meanRssiDbm = mean;
        candidate.tenthPercentileRssiDbm = values[p10Index];
        candidate.uniformityScore = uniformity * 100f;
        candidate.overallScore = 100f * (coverageWeight * coverageNormalized +
            tenthPercentileWeight * p10Normalized + uniformityWeight * uniformity) / weightTotal;
        return true;
    }

    private void ApplyRanking()
    {
        List<APCandidateData> ranked = new List<APCandidateData>(placement.Candidates);
        ranked.Sort((a, b) => { int score = b.overallScore.CompareTo(a.overallScore);
            return score != 0 ? score : string.CompareOrdinal(a.candidateId, b.candidateId); });
        for (int rankIndex = 0; rankIndex < ranked.Count; rankIndex++)
        {
            APCandidateData data = ranked[rankIndex];
            int rank = rankIndex + 1;
            CandidateAntennaMarker marker = FindMarker(data);
            if (rank <= 3) marker.SetRank(rank, data.overallScore);
            else { data.status = APCandidateStatus.Hidden; marker.RefreshVisual(); }
        }
        APCandidateData winner = ranked[0];
        floorField.ShowCandidateDataset(winner.heatmapCells);
        Debug.Log("[APOptimization] Rank 1 = " + winner.candidateId + " score=" + winner.overallScore.ToString("F1") + ".");
        Debug.Log("[APOptimization] Evaluation completed. Top three candidates remain visible.");
    }

    private CandidateAntennaMarker FindMarker(APCandidateData data)
    {
        for (int i = 0; i < placement.Markers.Count; i++)
            if (placement.Markers[i].Data == data) return placement.Markers[i];
        return null;
    }

    private void Fail(string message)
    {
        isEvaluating = false;
        placement.State = APPlacementState.EditingCandidates;
        for (int i = 0; i < placement.Candidates.Count; i++)
        {
            placement.Candidates[i].status = APCandidateStatus.Untested;
            placement.Markers[i].SetStatus(APCandidateStatus.Untested);
        }
        Reject(message);
    }

    public void ClearCandidateResults()
    {
        evaluationGeneration++;
        isEvaluating = false;
        if (floorField != null) floorField.ClearCandidateDataset();
    }

    private static void Reject(string message) { Debug.LogWarning("[APOptimization] " + message); }
}
