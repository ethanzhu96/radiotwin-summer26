using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RadioLensSionnaRouterEvaluator : MonoBehaviour
{
    [Header("Representative Receiver Sampling")]
    [SerializeField, Min(3)] private int maximumReceiverSamples = 5;

    [Header("Path-Gain Scoring")]
    [SerializeField] private float coverageThresholdPathGainDb = -90f;
    [SerializeField] private float weakPathGainDb = -140f;
    [SerializeField] private float strongPathGainDb = -40f;
    [SerializeField, Min(0f)] private float coverageWeight = .50f;
    [SerializeField, Min(0f)] private float tenthPercentileWeight = .30f;
    [SerializeField, Min(0f)] private float uniformityWeight = .20f;

    [Header("Dependencies")]
    [SerializeField] private RadioLensRoomContext roomContext;
    [SerializeField] private RadioLensSionnaClient sionnaClient;
    [SerializeField] private RadioLensSionnaPathRenderer pathRenderer;
    [SerializeField] private RadioLensPropagationController propagation;
    [SerializeField] private RtFloorField floorField;

    private int evaluationGeneration;
    private APPlacementManager activePlacement;
    public bool IsEvaluating { get; private set; }

    private sealed class CandidatePathVisual
    {
        public APCandidateData candidate;
        public List<SionnaPathDto> paths;
        public Vector3 txLocal;
        public Vector3 rxLocal;
        public double strongestCombinedGain;
    }

    public void RequestEvaluation(APPlacementManager placement)
    {
        ResolveDependencies();
        if (IsEvaluating || placement == null || placement.State == APPlacementState.Evaluating)
        { Reject("Candidate evaluation is already running."); return; }
        if (propagation == null || propagation.CurrentBackend != PropagationBackend.Sionna)
        { Reject("Sionna RT is not selected."); return; }
        if (placement.State != APPlacementState.EditingCandidates)
        { Reject("Start a new candidate round before evaluating again."); return; }
        if (placement.IsLogging) { Reject("Stop trajectory logging before evaluating router candidates."); return; }
        if (placement.Candidates.Count < placement.MinimumCandidates)
        { Reject("Place at least " + placement.MinimumCandidates + " router candidates."); return; }
        if (placement.MatchedRoomTransform == null) { Reject("Matched room is not ready."); return; }
        if (roomContext == null || !roomContext.IsReady) { Reject("Preparing room mesh. Try evaluation again when ready."); return; }
        if (floorField == null || !floorField.IsReadyForCandidateEvaluation || floorField.CoordinateFrameRoot == null)
        { Reject("Receiver grid is not ready."); return; }
        StartCoroutine(EvaluateRoutine(placement, ++evaluationGeneration));
    }

    public void CancelEvaluation()
    {
        evaluationGeneration++;
        IsEvaluating = false;
        StopAllCoroutines();
        ResetCancelledPlacement();
    }

    private IEnumerator EvaluateRoutine(APPlacementManager placement, int generation)
    {
        IsEvaluating = true;
        activePlacement = placement;
        placement.State = APPlacementState.Evaluating;
        int roomRevision = roomContext.Revision;
        string roomId = roomContext.RoomId;
        string meshVersion = roomContext.MeshVersion;
        string localizationId = roomContext.LocalizationId;
        List<Vector2Int> receiverIndices = SelectRepresentativeReceivers(floorField.GetEvaluationGridIndices());
        if (receiverIndices.Count == 0)
        { Fail(placement, "No representative receiver cells are available."); yield break; }

        for (int i = 0; i < placement.Candidates.Count; i++)
        {
            placement.Candidates[i].rank = 0;
            placement.Markers[i].SetStatus(APCandidateStatus.Queued);
        }
        propagation.SetStatus("Sionna router ranking: checking server...");
        bool healthOk = false; string failure = null;
        yield return sionnaClient.CheckHealth((ok, error) => { healthOk = ok; failure = error; });
        if (!IsCurrent(generation, roomRevision)) { FinishCancelled(); yield break; }
        if (!healthOk) { Fail(placement, "Sionna unavailable. " + failure); yield break; }

        propagation.SetStatus("Sionna router ranking: preparing room...");
        bool roomOk = false; string roomResult = null;
        yield return sionnaClient.EnsureRoom(roomContext, (ok, result) => { roomOk = ok; roomResult = result; });
        if (!IsCurrent(generation, roomRevision)) { FinishCancelled(); yield break; }
        if (!roomOk) { Fail(placement, roomResult); yield break; }

        List<CandidatePathVisual> visuals = new List<CandidatePathVisual>();
        int totalTraces = placement.Candidates.Count * receiverIndices.Count;
        int completedTraces = 0;
        for (int candidateIndex = 0; candidateIndex < placement.Candidates.Count; candidateIndex++)
        {
            APCandidateData candidate = placement.Candidates[candidateIndex];
            CandidateAntennaMarker marker = placement.Markers[candidateIndex];
            marker.SetStatus(APCandidateStatus.Evaluating);
            Vector3 txWorld = placement.MatchedRoomTransform.TransformPoint(candidate.roomLocalPosition);
            Vector3 txLocal = roomContext.RoomAnchor.InverseTransformPoint(txWorld);
            List<float> gainsDb = new List<float>(receiverIndices.Count);
            List<RtFloorField.RtCell> heatmap = new List<RtFloorField.RtCell>(receiverIndices.Count);
            CandidatePathVisual visual = new CandidatePathVisual
            {
                candidate = candidate,
                txLocal = txLocal,
                strongestCombinedGain = double.NegativeInfinity
            };

            for (int receiverIndex = 0; receiverIndex < receiverIndices.Count; receiverIndex++)
            {
                Vector2Int index = receiverIndices[receiverIndex];
                Vector3 floorLocal = floorField.GridIndexToFloorLocal(index);
                Vector3 rxGridLocal = new Vector3(floorLocal.x, floorField.floorY + floorField.ueHeight, floorLocal.z);
                Vector3 rxWorld = floorField.CoordinateFrameRoot.TransformPoint(rxGridLocal);
                Vector3 rxLocal = roomContext.RoomAnchor.InverseTransformPoint(rxWorld);
                string requestId = Guid.NewGuid().ToString();
                bool traceOk = false; string traceError = null; SionnaTraceResponseDto response = null;
                yield return sionnaClient.Trace(roomContext, txLocal, rxLocal, requestId,
                    (ok, error, value) => { traceOk = ok; traceError = error; response = value; });
                if (!IsCurrent(generation, roomRevision)) { FinishCancelled(); yield break; }
                if (!traceOk) { Fail(placement, "Sionna trace failed: " + traceError); yield break; }
                if (!ResponseMatches(response, requestId, roomId, meshVersion, localizationId))
                { Fail(placement, "Stale Sionna router result ignored."); yield break; }

                double combinedGain = SumLinearPathGain(response.paths);
                float pathGainDb = combinedGain > 0d
                    ? (float)(10d * Math.Log10(combinedGain))
                    : weakPathGainDb;
                gainsDb.Add(pathGainDb);
                heatmap.Add(new RtFloorField.RtCell
                {
                    index = index,
                    rxLocal = rxGridLocal,
                    rxWorld = rxWorld,
                    predictedRssiDb = pathGainDb,
                    paths = new List<RtPath>()
                });
                if (combinedGain > visual.strongestCombinedGain)
                {
                    visual.strongestCombinedGain = combinedGain;
                    visual.paths = response.paths;
                    visual.rxLocal = rxLocal;
                }
                completedTraces++;
                propagation.SetStatus("Sionna router ranking: " + completedTraces + "/" + totalTraces +
                    " traces (" + candidate.candidateId + ")");
            }

            candidate.heatmapCells = heatmap;
            CalculateMetrics(candidate, gainsDb);
            candidate.status = APCandidateStatus.Evaluated;
            visuals.Add(visual);
            Debug.Log("[SionnaRouter] " + candidate.candidateId + " score=" + candidate.overallScore.ToString("F1") +
                " coverage=" + candidate.coveragePercent.ToString("F1") + "% meanGain=" +
                candidate.meanRssiDbm.ToString("F1") + "dB p10Gain=" + candidate.tenthPercentileRssiDbm.ToString("F1") + "dB.");
        }

        List<APCandidateData> ranked = new List<APCandidateData>(placement.Candidates);
        ranked.Sort((a, b) =>
        {
            int score = b.overallScore.CompareTo(a.overallScore);
            return score != 0 ? score : string.CompareOrdinal(a.candidateId, b.candidateId);
        });
        for (int i = 0; i < ranked.Count; i++)
        {
            CandidateAntennaMarker marker = FindMarker(placement, ranked[i]);
            if (i < 3) marker?.SetRank(i + 1, ranked[i].overallScore);
            else if (marker != null) { ranked[i].status = APCandidateStatus.Hidden; marker.RefreshVisual(); }
        }
        APCandidateData winner = ranked[0];
        floorField.ShowCandidateDataset(winner.heatmapCells);
        CandidatePathVisual winnerVisual = visuals.Find(item => item.candidate == winner);
        int pathCount = winnerVisual != null && winnerVisual.paths != null
            ? pathRenderer.RenderPaths(winnerVisual.paths, roomContext.RoomAnchor, sionnaClient.TopK,
                winnerVisual.txLocal, winnerVisual.rxLocal)
            : 0;
        IsEvaluating = false;
        activePlacement = null;
        placement.State = APPlacementState.ShowingResults;
        propagation.SetStatus("Sionna RT winner: " + winner.candidateId + " score " +
            winner.overallScore.ToString("F1") + " (" + receiverIndices.Count + " Rx, " + pathCount + " paths)");
        Debug.Log("[SionnaRouter] Ranking complete. Winner=" + winner.candidateId +
            " representativeRx=" + receiverIndices.Count + " renderedPaths=" + pathCount + ".");
    }

    private List<Vector2Int> SelectRepresentativeReceivers(List<Vector2Int> all)
    {
        List<Vector2Int> selected = new List<Vector2Int>();
        if (all == null || all.Count == 0) return selected;
        int desired = Mathf.Min(Mathf.Max(3, maximumReceiverSamples), all.Count);
        for (int i = 0; i < desired; i++)
        {
            int source = desired == 1 ? 0 : Mathf.RoundToInt(i * (all.Count - 1f) / (desired - 1f));
            Vector2Int value = all[Mathf.Clamp(source, 0, all.Count - 1)];
            if (!selected.Contains(value)) selected.Add(value);
        }
        return selected;
    }

    private void CalculateMetrics(APCandidateData candidate, List<float> values)
    {
        values.Sort();
        double sum = 0d;
        int covered = 0;
        for (int i = 0; i < values.Count; i++)
        { sum += values[i]; if (values[i] >= coverageThresholdPathGainDb) covered++; }
        float mean = (float)(sum / values.Count);
        int p10Index = Mathf.Clamp(Mathf.CeilToInt(values.Count * .1f) - 1, 0, values.Count - 1);
        double variance = 0d;
        for (int i = 0; i < values.Count; i++) { double delta = values[i] - mean; variance += delta * delta; }
        float deviation = Mathf.Sqrt((float)(variance / values.Count));
        float coverage = covered / (float)values.Count;
        float p10 = Mathf.InverseLerp(weakPathGainDb, strongPathGainDb, values[p10Index]);
        float uniformity = Mathf.Clamp01(1f - deviation / 40f);
        float weightTotal = Mathf.Max(.0001f, coverageWeight + tenthPercentileWeight + uniformityWeight);
        candidate.coveragePercent = coverage * 100f;
        candidate.meanRssiDbm = mean;
        candidate.tenthPercentileRssiDbm = values[p10Index];
        candidate.uniformityScore = uniformity * 100f;
        candidate.overallScore = 100f * (coverageWeight * coverage + tenthPercentileWeight * p10 +
            uniformityWeight * uniformity) / weightTotal;
    }

    private bool IsCurrent(int generation, int roomRevision) => generation == evaluationGeneration &&
        propagation != null && propagation.CurrentBackend == PropagationBackend.Sionna &&
        roomContext != null && roomContext.IsReady && roomContext.Revision == roomRevision;

    private static bool ResponseMatches(SionnaTraceResponseDto response, string requestId, string roomId,
        string meshVersion, string localizationId) => response != null && response.requestId == requestId &&
        response.roomId == roomId && response.meshVersion == meshVersion &&
        response.localizationId == localizationId && response.coordinateFrame == SionnaProtocol.CoordinateFrame;

    private static double SumLinearPathGain(IReadOnlyList<SionnaPathDto> paths)
    {
        double sum = 0d;
        if (paths != null) for (int i = 0; i < paths.Count; i++)
            if (paths[i] != null && paths[i].pathGain > 0d && !double.IsNaN(paths[i].pathGain)) sum += paths[i].pathGain;
        return sum;
    }

    private static CandidateAntennaMarker FindMarker(APPlacementManager placement, APCandidateData candidate)
    {
        for (int i = 0; i < placement.Markers.Count; i++)
            if (placement.Markers[i] != null && placement.Markers[i].Data == candidate) return placement.Markers[i];
        return null;
    }

    private void Fail(APPlacementManager placement, string message)
    {
        IsEvaluating = false;
        activePlacement = null;
        placement.State = APPlacementState.EditingCandidates;
        for (int i = 0; i < placement.Candidates.Count; i++)
        {
            placement.Candidates[i].rank = 0;
            placement.Markers[i].SetStatus(APCandidateStatus.Untested);
        }
        Reject(message);
    }

    private void FinishCancelled()
    {
        IsEvaluating = false;
        ResetCancelledPlacement();
        Debug.LogWarning("[SionnaRouter] Evaluation cancelled or stale response ignored.");
    }

    private void ResetCancelledPlacement()
    {
        if (activePlacement == null) return;
        if (activePlacement.State == APPlacementState.Evaluating)
            activePlacement.State = APPlacementState.EditingCandidates;
        for (int i = 0; i < activePlacement.Candidates.Count && i < activePlacement.Markers.Count; i++)
        {
            activePlacement.Candidates[i].rank = 0;
            activePlacement.Markers[i].SetStatus(APCandidateStatus.Untested);
        }
        activePlacement = null;
    }

    private void Reject(string message)
    {
        propagation?.SetStatus(message);
        Debug.LogWarning("[SionnaRouter] " + message);
    }

    private void ResolveDependencies()
    {
        if (roomContext == null) roomContext = FindFirstObjectByType<RadioLensRoomContext>();
        if (sionnaClient == null) sionnaClient = FindFirstObjectByType<RadioLensSionnaClient>();
        if (pathRenderer == null) pathRenderer = FindFirstObjectByType<RadioLensSionnaPathRenderer>();
        if (propagation == null) propagation = FindFirstObjectByType<RadioLensPropagationController>();
        if (floorField == null) floorField = FindFirstObjectByType<RtFloorField>();
    }

    private void OnDisable() { CancelEvaluation(); }
}
