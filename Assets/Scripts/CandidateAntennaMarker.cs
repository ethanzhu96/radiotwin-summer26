using UnityEngine;

public class CandidateAntennaMarker : MonoBehaviour
{
    private APCandidateData data;
    private APPlacementManager owner;
    private Renderer[] renderers;
    private TextMesh label;
    private bool nearby;
    private float deleteHeld;
    private MaterialPropertyBlock properties;

    public APCandidateData Data => data;

    public void Initialize(APCandidateData candidateData, APPlacementManager placementOwner, Transform leftController)
    {
        data = candidateData;
        owner = placementOwner;
        renderers = GetComponentsInChildren<Renderer>(true);
        label = GetComponentInChildren<TextMesh>(true);
        properties = new MaterialPropertyBlock();
        RefreshVisual();
    }

    private void Update()
    {
        if (owner == null || data == null) return;
        bool canDelete = owner.State == APPlacementState.EditingCandidates;
        if (!canDelete && nearby)
        {
            nearby = false;
            deleteHeld = 0f;
            RefreshVisual();
        }

        if (!nearby) return;
        if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch))
        {
            deleteHeld += Time.unscaledDeltaTime;
            SetDeletionProgress(deleteHeld / Mathf.Max(owner.DeletionHoldDuration, 0.05f));
            if (deleteHeld >= owner.DeletionHoldDuration)
            {
                nearby = false;
                owner.DeleteCandidate(this);
            }
        }
        else if (deleteHeld > 0f)
        {
            deleteHeld = 0f;
            SetDeletionProgress(0f);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (owner == null || owner.State != APPlacementState.EditingCandidates ||
            other != owner.ProximityCollider) return;
        nearby = true;
        deleteHeld = 0f;
        RefreshVisual();
        owner.PulseLeftHaptics();
    }

    private void OnTriggerExit(Collider other)
    {
        if (owner == null || other != owner.ProximityCollider) return;
        nearby = false;
        deleteHeld = 0f;
        RefreshVisual();
    }

    public void SetStatus(APCandidateStatus status) { data.status = status; RefreshVisual(); }
    public void SetRank(int rank, float score) { data.rank = rank; data.overallScore = score; data.status = APCandidateStatus.Ranked; RefreshVisual(); }

    public void SetDeletionProgress(float progress)
    {
        Color color = Color.Lerp(nearby ? new Color(1f, .15f, .15f, .85f) : BaseColor(), Color.white, Mathf.Clamp01(progress));
        ApplyColor(color);
    }

    public void RefreshVisual()
    {
        if (data == null) return;
        Color color = nearby ? new Color(1f, .15f, .15f, .9f) : BaseColor();
        ApplyColor(color);
        if (label != null)
        {
            string name = "Candidate " + data.candidateId.Substring(data.candidateId.Length - 1);
            label.text = data.rank > 0
                ? name + "\n#" + data.rank + "\nScore: " + data.overallScore.ToString("F1")
                : name + "\n" + (data.status == APCandidateStatus.Evaluating ? "Evaluating" : "Untested");
        }
        gameObject.SetActive(data.status != APCandidateStatus.Hidden);
    }

    private Color BaseColor()
    {
        if (data.rank == 1) return new Color(.1f, 1f, .2f, 1f);
        if (data.rank == 2) return new Color(1f, .9f, .05f, 1f);
        if (data.rank == 3) return new Color(1f, .4f, .05f, 1f);
        return new Color(1f, .05f, .05f, data.status == APCandidateStatus.Hidden ? .08f : .48f);
    }

    private void ApplyColor(Color color)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || renderer is LineRenderer) continue;
            renderer.GetPropertyBlock(properties);
            properties.SetColor("_Color", color);
            properties.SetColor("_BaseColor", color);
            properties.SetColor("_EmissionColor", color * .5f);
            renderer.SetPropertyBlock(properties);
        }
    }
}
