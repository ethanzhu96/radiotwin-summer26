using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class RFDeadZoneData
{
    public string zoneId;
    public List<Vector2Int> indices = new List<Vector2Int>();
    public float area;
    public float meanRssiDb;
    public float p10RssiDb;
    public float minimumRssiDb;
    public Vector3 weightedCentroidWorld;
    public float severity;
}

[Serializable]
public class RecommendedReflectorData
{
    public string reflectorId;
    public string targetDeadZoneId;
    public Vector3 roomLocalPosition;
    public Quaternion roomLocalRotation;
    public float widthMeters;
    public float heightMeters;
    public float thicknessMeters;
    public float meanImprovementDb;
    public float p10ImprovementDb;
    public float recoveredCoveragePercent;
    public float overallScore;
    public int sequentialOrder;
}

public class ReflectorOptimizationController : MonoBehaviour
{
    [Header("Assumed Tx")]
    [SerializeField] private float assumedTxForwardOffset = 0.25f;
    [SerializeField] private Vector3 assumedTxLocalRotationOffset;
    [SerializeField] private float deleteProximity = 0.16f;
    [SerializeField] private float deleteHoldSeconds = 0.5f;

    [Header("Dead Zones")]
    [SerializeField] private float deadZoneThresholdDb = -70f;
    [SerializeField] private int minimumDeadZoneCells = 4;
    [SerializeField] private float minimumDeadZoneArea = 0.5f;

    [Header("Reflector Search")]
    [SerializeField] private float reflectorWidth = 0.6f;
    [SerializeField] private float reflectorHeight = 0.8f;
    [SerializeField] private float reflectorThickness = 0.025f;
    [SerializeField] private int maximumMountingSamples = 8;
    [SerializeField] private int maximumSearchConfigurations = 24;
    [SerializeField] private float angleSearchDegrees = 10f;
    [SerializeField] private float angleStepDegrees = 10f;
    [SerializeField] private float minimumP10ImprovementDb = 2f;
    [SerializeField] private float minimumRecoveredCoveragePercent = 5f;
    [SerializeField] private int cellsPerFrame = 4;

    private readonly List<RecommendedReflectorData> recommendations = new List<RecommendedReflectorData>();
    private readonly List<RtSurfaceTriangle> acceptedTriangles = new List<RtSurfaceTriangle>();
    private readonly List<GameObject> reflectorVisuals = new List<GameObject>();
    private APPlacementState phase = APPlacementState.EditingCandidates;
    private Transform leftController;
    private Transform assumedTx;
    private Transform container;
    private WifiPoseLogger logger;
    private RtFloorField floorField;
    private SimpleRayTracer tracer;
    private SceneColliderBaker baker;
    private float deleteHeld;
    private bool assumedTxNearby;
    private Renderer[] assumedTxRenderers;
    private int evaluationId;
    private List<RtFloorField.RtCell> latestCells;

    public bool Active { get; set; }
    public bool IsEvaluating => phase == APPlacementState.Evaluating;
    public bool HasAssumedTx => assumedTx != null;
    public int RecommendationCount => recommendations.Count;
    public string PhaseName => phase.ToString();
    public IReadOnlyList<RecommendedReflectorData> Recommendations => recommendations;

    public bool TryGetAssumedTxTransform(out Transform tx)
    {
        tx = assumedTx;
        return tx != null;
    }

    public void Initialize(Transform controller)
    {
        if (controller != null) leftController = controller;
        ResolveDependencies();
    }

    private void Update()
    {
        if (!Active || phase == APPlacementState.Evaluating || assumedTx == null || leftController == null) return;
        bool near = (leftController.position - assumedTx.position).sqrMagnitude <= deleteProximity * deleteProximity;
        if (near != assumedTxNearby)
        {
            assumedTxNearby = near;
            SetAssumedTxHighlight(near);
            if (near) StartCoroutine(ProximityHaptic());
        }
        if (near && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch))
        {
            deleteHeld += Time.unscaledDeltaTime;
            if (deleteHeld >= deleteHoldSeconds) RemoveAssumedTx();
        }
        else deleteHeld = 0f;
    }

    private void ResolveDependencies()
    {
        if (logger == null) logger = FindFirstObjectByType<WifiPoseLogger>();
        if (floorField == null) floorField = FindFirstObjectByType<RtFloorField>();
        if (tracer == null) tracer = FindFirstObjectByType<SimpleRayTracer>();
        if (baker == null) baker = FindFirstObjectByType<SceneColliderBaker>();
        Transform room = RoomAlignmentManager.Instance != null ? RoomAlignmentManager.Instance.DatasetRoot : null;
        if (container == null && room != null)
        {
            container = new GameObject("ReflectorOptimizationContainer").transform;
            container.SetParent(room, false);
        }
    }

    public void HandlePlacePressed()
    {
        ResolveDependencies();
        if (phase == APPlacementState.Evaluating) { Report("Wait for reflector evaluation to finish."); return; }
        if (logger != null && logger.isLogging) { Report("Stop logging before placing the Assumed Tx."); return; }
        if (assumedTx != null) { Report("Remove the current Assumed Tx before placing another."); return; }
        if (leftController == null || container == null) { Report("Left controller or matched room is not ready."); return; }
        Vector3 horizontal = Vector3.ProjectOnPlane(leftController.forward, Vector3.up);
        Quaternion yaw = horizontal.sqrMagnitude > .0001f ? Quaternion.LookRotation(horizontal.normalized, Vector3.up) : Quaternion.identity;
        Vector3 position = leftController.position + leftController.forward * assumedTxForwardOffset;
        GameObject marker = CreateAntenna("Assumed Tx", new Color(1f, .15f, .15f, .85f));
        marker.transform.SetPositionAndRotation(position, yaw * Quaternion.Euler(assumedTxLocalRotationOffset));
        marker.transform.SetParent(container, true);
        assumedTx = marker.transform;
        assumedTxRenderers = marker.GetComponentsInChildren<Renderer>(true);
        assumedTxNearby = false;
        phase = APPlacementState.EditingCandidates;
        Debug.Log("[AssumedTx] Placed at room-local " + container.parent.InverseTransformPoint(position).ToString("F3") + ".");
    }

    public void RemoveAssumedTx()
    {
        evaluationId++;
        if (assumedTx != null) Destroy(assumedTx.gameObject);
        assumedTx = null;
        assumedTxRenderers = null;
        assumedTxNearby = false;
        ClearReflectorResults();
        phase = APPlacementState.EditingCandidates;
        deleteHeld = 0f;
        Debug.Log("[AssumedTx] Removed; reflector-specific results cleared.");
    }

    private void SetAssumedTxHighlight(bool highlighted)
    {
        if (assumedTxRenderers == null) return;
        Color color = highlighted ? new Color(1f, .55f, .2f, 1f) : new Color(1f, .15f, .15f, .85f);
        for (int i = 0; i < assumedTxRenderers.Length; i++)
            if (assumedTxRenderers[i] != null) assumedTxRenderers[i].material.color = color;
    }

    private IEnumerator ProximityHaptic()
    {
        OVRInput.SetControllerVibration(.25f, .25f, OVRInput.Controller.LTouch);
        yield return new WaitForSecondsRealtime(.06f);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
    }

    public void RequestAnalysis()
    {
        ResolveDependencies();
        if (IsEvaluating) { Report("Evaluation already running."); return; }
        if (assumedTx == null) { Report("Place an Assumed Tx before evaluation."); return; }
        if (logger != null && logger.isLogging) { Report("Stop logging before evaluation."); return; }
        if (floorField == null || tracer == null || baker == null || !floorField.IsReadyForCandidateEvaluation)
        { Report("RT pipeline is not ready."); return; }
        StartCoroutine(AnalyzeRoutine(++evaluationId));
    }

    private IEnumerator AnalyzeRoutine(int id)
    {
        phase = APPlacementState.Evaluating;
        ClearReflectorResults();
        Debug.Log("[ReflectorMode] Generating baseline heatmap.");
        List<Vector2Int> grid = floorField.GetEvaluationGridIndices();
        yield return EvaluateField(assumedTx.position, grid, cells => latestCells = cells);
        if (id != evaluationId) yield break;
        List<RFDeadZoneData> zones = DetectDeadZones(latestCells);
        int initialZoneCount = zones.Count;
        Debug.Log("[DeadZone] Detected " + zones.Count + " significant dead zones.");
        bool usingWeakestAreaFallback = false;
        if (zones.Count == 0)
        {
            RFDeadZoneData weakestArea = BuildWeakestArea(latestCells);
            if (weakestArea != null)
            {
                zones.Add(weakestArea);
                usingWeakestAreaFallback = true;
                Debug.Log("[DeadZone] No significant dead zone; targeting " + weakestArea.zoneId +
                    " at mean=" + weakestArea.meanRssiDb.ToString("F1") + " dB.");
            }
        }

        for (int order = 1; order <= 2 && zones.Count > 0; order++)
        {
            Debug.Log("[ReflectorSearch] Optimizing Reflector " + order + " for " + zones[0].zoneId + ".");
            SearchResult best = null;
            yield return SearchReflector(assumedTx.position, zones[0], latestCells, result => best = result);
            if (id != evaluationId) yield break;
            if (best == null || (!usingWeakestAreaFallback && best.p10Improvement < minimumP10ImprovementDb &&
                best.recoveredCoverage < minimumRecoveredCoveragePercent))
            {
                Debug.LogWarning("[ReflectorSearch] No useful reflector placement was found for " + zones[0].zoneId + ".");
                break;
            }
            AcceptReflector(best, zones[0], order);
            Debug.Log("[ReflectorResult] Reflector " + order + " accepted; recomputing coverage.");
            yield return EvaluateField(assumedTx.position, grid, cells => latestCells = cells);
            if (usingWeakestAreaFallback) { zones.Clear(); break; }
            zones = DetectDeadZones(latestCells);
        }

        floorField.ShowCandidateDataset(latestCells);
        phase = APPlacementState.ShowingResults;
        Debug.Log("[ReflectorResult] Complete. Initial dead zones=" + initialZoneCount +
            " reflectors=" + recommendations.Count + " remaining significant zones=" + zones.Count + ".");
    }

    private IEnumerator EvaluateField(Vector3 tx, List<Vector2Int> grid, Action<List<RtFloorField.RtCell>> completed)
    {
        tracer.SetEngineeredReflectors(acceptedTriangles);
        List<RtFloorField.RtCell> cells = new List<RtFloorField.RtCell>(grid.Count);
        for (int i = 0; i < grid.Count; i++)
        {
            RtFloorField.RtCell cell = floorField.EvaluateCell(tx, grid[i]);
            if (cell != null) cells.Add(cell);
            if ((i + 1) % Mathf.Max(1, cellsPerFrame) == 0) yield return null;
        }
        completed(cells);
    }

    private List<RFDeadZoneData> DetectDeadZones(List<RtFloorField.RtCell> cells)
    {
        Dictionary<Vector2Int, RtFloorField.RtCell> weak = new Dictionary<Vector2Int, RtFloorField.RtCell>();
        for (int i = 0; i < cells.Count; i++) if (!float.IsNaN(cells[i].predictedRssiDb) &&
            cells[i].predictedRssiDb < deadZoneThresholdDb) weak[cells[i].index] = cells[i];
        HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
        List<RFDeadZoneData> zones = new List<RFDeadZoneData>();
        foreach (KeyValuePair<Vector2Int, RtFloorField.RtCell> seed in weak)
        {
            if (!visited.Add(seed.Key)) continue;
            Queue<Vector2Int> queue = new Queue<Vector2Int>(); queue.Enqueue(seed.Key);
            RFDeadZoneData zone = new RFDeadZoneData();
            List<float> values = new List<float>(); Vector3 weighted = Vector3.zero; float weightSum = 0f;
            while (queue.Count > 0)
            {
                Vector2Int index = queue.Dequeue(); RtFloorField.RtCell cell = weak[index];
                zone.indices.Add(index); values.Add(cell.predictedRssiDb);
                float weight = Mathf.Max(.01f, deadZoneThresholdDb - cell.predictedRssiDb);
                weighted += cell.rxWorld * weight; weightSum += weight;
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
                {
                    if (x == 0 && y == 0) continue; Vector2Int neighbor = index + new Vector2Int(x, y);
                    if (weak.ContainsKey(neighbor) && visited.Add(neighbor)) queue.Enqueue(neighbor);
                }
            }
            zone.area = zone.indices.Count * floorField.cellSize * floorField.cellSize;
            if (zone.indices.Count < minimumDeadZoneCells || zone.area < minimumDeadZoneArea) continue;
            values.Sort(); float sum = 0f; for (int i = 0; i < values.Count; i++) sum += values[i];
            zone.meanRssiDb = sum / values.Count; zone.minimumRssiDb = values[0];
            zone.p10RssiDb = values[Mathf.Clamp(Mathf.CeilToInt(values.Count * .1f) - 1, 0, values.Count - 1)];
            zone.weightedCentroidWorld = weighted / weightSum;
            zone.severity = zone.area * Mathf.Max(0f, deadZoneThresholdDb - zone.meanRssiDb);
            zones.Add(zone);
        }
        zones.Sort((a, b) => b.severity.CompareTo(a.severity));
        for (int i = 0; i < zones.Count; i++) zones[i].zoneId = "DEAD_ZONE_" + (i + 1);
        return zones;
    }

    private RFDeadZoneData BuildWeakestArea(List<RtFloorField.RtCell> cells)
    {
        if (cells == null || cells.Count == 0) return null;
        RtFloorField.RtCell weakest = null;
        Dictionary<Vector2Int, RtFloorField.RtCell> lookup = new Dictionary<Vector2Int, RtFloorField.RtCell>();
        for (int i = 0; i < cells.Count; i++)
        {
            RtFloorField.RtCell cell = cells[i];
            if (cell == null || float.IsNaN(cell.predictedRssiDb) || float.IsInfinity(cell.predictedRssiDb)) continue;
            lookup[cell.index] = cell;
            if (weakest == null || cell.predictedRssiDb < weakest.predictedRssiDb) weakest = cell;
        }
        if (weakest == null) return null;

        RFDeadZoneData area = new RFDeadZoneData { zoneId = "WEAKEST_AREA_1" };
        List<float> values = new List<float>();
        Vector3 weighted = Vector3.zero;
        float weightSum = 0f;
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
        {
            Vector2Int index = weakest.index + new Vector2Int(x, y);
            if (!lookup.TryGetValue(index, out RtFloorField.RtCell cell)) continue;
            area.indices.Add(index);
            values.Add(cell.predictedRssiDb);
            float weight = Mathf.Max(1f, floorField.StrongRssiDb - cell.predictedRssiDb);
            weighted += cell.rxWorld * weight;
            weightSum += weight;
        }
        if (values.Count == 0) return null;
        values.Sort();
        float sum = 0f; for (int i = 0; i < values.Count; i++) sum += values[i];
        area.area = values.Count * floorField.cellSize * floorField.cellSize;
        area.meanRssiDb = sum / values.Count;
        area.minimumRssiDb = values[0];
        area.p10RssiDb = values[Mathf.Clamp(Mathf.CeilToInt(values.Count * .1f) - 1, 0, values.Count - 1)];
        area.weightedCentroidWorld = weighted / Mathf.Max(weightSum, .0001f);
        area.severity = Mathf.Max(.01f, floorField.StrongRssiDb - area.meanRssiDb) * area.area;
        return area;
    }

    private class SearchResult
    {
        public Vector3 center; public Quaternion rotation; public List<RtSurfaceTriangle> triangles;
        public float p10Improvement; public float meanImprovement; public float recoveredCoverage; public float score;
    }

    private IEnumerator SearchReflector(Vector3 tx, RFDeadZoneData zone, List<RtFloorField.RtCell> baseline, Action<SearchResult> done)
    {
        Dictionary<Vector2Int, float> baselineValues = new Dictionary<Vector2Int, float>();
        for (int i = 0; i < baseline.Count; i++) baselineValues[baseline[i].index] = baseline[i].predictedRssiDb;
        List<RtSurfaceTriangle> mounts = new List<RtSurfaceTriangle>();
        IReadOnlyList<RtSurfaceTriangle> surfaces = baker.SurfaceTriangles;
        for (int i = 0; i < surfaces.Count && mounts.Count < maximumMountingSamples; i++)
            if (Mathf.Abs(Vector3.Dot(surfaces[i].normalWorld, Vector3.up)) < .35f) mounts.Add(surfaces[i]);
        SearchResult best = null; int tested = 0;
        for (int m = 0; m < mounts.Count && tested < maximumSearchConfigurations; m++)
        {
            Vector3 center = (mounts[m].pointAWorld + mounts[m].pointBWorld + mounts[m].pointCWorld) / 3f;
            Vector3 normal = ((tx - center).normalized + (zone.weightedCentroidWorld - center).normalized).normalized;
            if (normal.sqrMagnitude < .01f) normal = mounts[m].normalWorld;
            Quaternion initial = Quaternion.LookRotation(normal, Vector3.up);
            for (float yaw = -angleSearchDegrees; yaw <= angleSearchDegrees && tested < maximumSearchConfigurations; yaw += Mathf.Max(1f, angleStepDegrees))
            for (float pitch = -angleSearchDegrees; pitch <= angleSearchDegrees && tested < maximumSearchConfigurations; pitch += Mathf.Max(1f, angleStepDegrees))
            {
                Quaternion yawRotation = Quaternion.AngleAxis(yaw, Vector3.up) * initial;
                Quaternion rotation = Quaternion.AngleAxis(pitch, yawRotation * Vector3.right) * yawRotation;
                List<RtSurfaceTriangle> trialPanel = BuildPanel(center, rotation);
                List<RtSurfaceTriangle> trial = new List<RtSurfaceTriangle>(acceptedTriangles); trial.AddRange(trialPanel);
                tracer.SetEngineeredReflectors(trial);
                List<float> before = new List<float>(); List<float> after = new List<float>(); int recovered = 0;
                for (int i = 0; i < zone.indices.Count; i++)
                {
                    Vector2Int index = zone.indices[i]; if (!baselineValues.TryGetValue(index, out float oldValue)) continue;
                    RtFloorField.RtCell result = floorField.EvaluateCell(tx, index); if (result == null) continue;
                    before.Add(oldValue); after.Add(result.predictedRssiDb);
                    if (oldValue < deadZoneThresholdDb && result.predictedRssiDb >= deadZoneThresholdDb) recovered++;
                    if ((i + 1) % Mathf.Max(1, cellsPerFrame) == 0) yield return null;
                }
                float oldMean = Mean(before), newMean = Mean(after);
                float oldP10 = P10(before), newP10 = P10(after);
                float recovery = before.Count > 0 ? recovered * 100f / before.Count : 0f;
                float score = .5f * recovery + .3f * Mathf.Max(0f, newP10 - oldP10) * 10f + .2f * Mathf.Max(0f, newMean - oldMean) * 10f;
                if (best == null || score > best.score) best = new SearchResult { center = center, rotation = rotation,
                    triangles = trialPanel, p10Improvement = newP10 - oldP10, meanImprovement = newMean - oldMean,
                    recoveredCoverage = recovery, score = score };
                tested++;
            }
        }
        tracer.SetEngineeredReflectors(acceptedTriangles);
        done(best);
    }

    private List<RtSurfaceTriangle> BuildPanel(Vector3 center, Quaternion rotation)
    {
        Vector3 right = rotation * Vector3.right * reflectorWidth * .5f;
        Vector3 up = rotation * Vector3.up * reflectorHeight * .5f;
        Vector3 normal = rotation * Vector3.forward;
        Vector3 a = center - right - up, b = center + right - up, c = center + right + up, d = center - right + up;
        return new List<RtSurfaceTriangle> {
            new RtSurfaceTriangle { pointAWorld=a, pointBWorld=b, pointCWorld=c, normalWorld=normal, area=reflectorWidth*reflectorHeight*.5f, isEngineeredReflector=true },
            new RtSurfaceTriangle { pointAWorld=a, pointBWorld=c, pointCWorld=d, normalWorld=normal, area=reflectorWidth*reflectorHeight*.5f, isEngineeredReflector=true }};
    }

    private void AcceptReflector(SearchResult result, RFDeadZoneData zone, int order)
    {
        acceptedTriangles.AddRange(result.triangles); tracer.SetEngineeredReflectors(acceptedTriangles);
        Transform room = container.parent;
        RecommendedReflectorData data = new RecommendedReflectorData { reflectorId = "REFLECTOR_" + order,
            targetDeadZoneId = zone.zoneId, roomLocalPosition = room.InverseTransformPoint(result.center),
            roomLocalRotation = Quaternion.Inverse(room.rotation) * result.rotation, widthMeters = reflectorWidth,
            heightMeters = reflectorHeight, thicknessMeters = reflectorThickness, meanImprovementDb = result.meanImprovement,
            p10ImprovementDb = result.p10Improvement, recoveredCoveragePercent = result.recoveredCoverage,
            overallScore = result.score, sequentialOrder = order };
        recommendations.Add(data);
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = data.reflectorId; visual.transform.SetPositionAndRotation(result.center, result.rotation);
        visual.transform.localScale = new Vector3(reflectorWidth, reflectorHeight, reflectorThickness);
        visual.transform.SetParent(container, true);
        Renderer renderer = visual.GetComponent<Renderer>(); renderer.material.color = order == 1 ? Color.green : Color.cyan;
        GameObject labelObject = new GameObject("Label"); labelObject.transform.SetParent(visual.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, .6f, 0f); labelObject.transform.localScale = Vector3.one * 2f;
        TextMesh label = labelObject.AddComponent<TextMesh>(); label.anchor = TextAnchor.MiddleCenter; label.characterSize = .025f;
        label.text = "Reflector " + order + "\n" + zone.zoneId + "\nP10 +" + result.p10Improvement.ToString("F1") + " dB";
        reflectorVisuals.Add(visual);

        GameObject target = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        target.name = zone.zoneId + "_Target";
        target.transform.position = zone.weightedCentroidWorld;
        target.transform.localScale = Vector3.one * .10f;
        target.transform.SetParent(container, true);
        target.GetComponent<Renderer>().material.color = new Color(1f, .15f, .05f, .65f);
        Collider targetCollider = target.GetComponent<Collider>(); if (targetCollider != null) Destroy(targetCollider);
        reflectorVisuals.Add(target);
    }

    public void SetVisualsVisible(bool visible)
    {
        if (container != null) container.gameObject.SetActive(visible);
        if (visible && latestCells != null) floorField.ShowCandidateDataset(latestCells);
    }

    public void RestoreHeatmap() { if (latestCells != null && floorField != null) floorField.ShowCandidateDataset(latestCells); }

    private void ClearReflectorResults()
    {
        recommendations.Clear(); acceptedTriangles.Clear(); tracer?.ClearEngineeredReflectors(); latestCells = null;
        for (int i = 0; i < reflectorVisuals.Count; i++) if (reflectorVisuals[i] != null) Destroy(reflectorVisuals[i]);
        reflectorVisuals.Clear(); floorField?.ClearCandidateDataset();
    }

    private static float Mean(List<float> values) { if (values.Count == 0) return -120f; float sum=0f; for(int i=0;i<values.Count;i++)sum+=values[i]; return sum/values.Count; }
    private static float P10(List<float> values) { if(values.Count==0)return -120f; values.Sort(); return values[Mathf.Clamp(Mathf.CeilToInt(values.Count*.1f)-1,0,values.Count-1)]; }

    private static GameObject CreateAntenna(string name, Color color)
    {
        GameObject root = new GameObject(name);
        GameObject mast = GameObject.CreatePrimitive(PrimitiveType.Cylinder); mast.transform.SetParent(root.transform, false);
        mast.transform.localPosition = Vector3.up * .18f; mast.transform.localScale = new Vector3(.025f,.18f,.025f);
        mast.GetComponent<Renderer>().material.color = color; Destroy(mast.GetComponent<Collider>());
        GameObject tip = GameObject.CreatePrimitive(PrimitiveType.Sphere); tip.transform.SetParent(root.transform, false);
        tip.transform.localPosition = Vector3.up*.4f; tip.transform.localScale=Vector3.one*.09f;
        tip.GetComponent<Renderer>().material.color=color; Destroy(tip.GetComponent<Collider>());
        GameObject textObject = new GameObject("Label"); textObject.transform.SetParent(root.transform,false); textObject.transform.localPosition=Vector3.up*.55f;
        TextMesh text=textObject.AddComponent<TextMesh>(); text.text=name; text.anchor=TextAnchor.MiddleCenter; text.characterSize=.025f;
        return root;
    }

    private static void Report(string message) { Debug.LogWarning("[ReflectorMode] " + message); }
}
