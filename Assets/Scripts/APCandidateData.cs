using System;
using System.Collections.Generic;
using UnityEngine;

public enum APCandidateStatus { Preview, Untested, Queued, Evaluating, Evaluated, Ranked, Hidden }
public enum APPlacementState { EditingCandidates, Evaluating, ShowingResults }

[Serializable]
public class APCandidateData
{
    public string candidateId;
    public Vector3 roomLocalPosition;
    public Quaternion roomLocalRotation = Quaternion.identity;
    public APCandidateStatus status = APCandidateStatus.Untested;
    public float overallScore;
    public float coveragePercent;
    public float meanRssiDbm;
    public float tenthPercentileRssiDbm;
    public float uniformityScore;
    public int rank;

    [NonSerialized] public List<RtFloorField.RtCell> heatmapCells;
}
