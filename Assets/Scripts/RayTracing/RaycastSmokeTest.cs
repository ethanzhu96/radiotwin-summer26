using UnityEngine;

/*
Inspector setup:
- Add this script to any active GameObject in the scene.
- Set Ray Origin to the Quest controller transform or CenterEyeAnchor.
- Set RT Scene Mask to the layer used by your room/effect mesh colliders.
- Optional: assign Hit Marker to a small sphere so it moves to the ray hit point.
- This only verifies that Physics.Raycast can hit the reconstructed room mesh.
*/
public class RaycastSmokeTest : MonoBehaviour
{
    public Transform rayOrigin;
    public LayerMask rtSceneMask;
    public float maxDistance = 10f;
    public Transform hitMarker;

    private float nextLogTime;
    private Collider lastLoggedCollider;

    void Update()
    {
        CastSmokeRay(false);
    }

    [ContextMenu("Cast Smoke Ray Once")]
    public void CastSmokeRayOnce()
    {
        CastSmokeRay(true);
    }

    private void CastSmokeRay(bool forceLog)
    {
        if (rayOrigin == null)
        {
            return;
        }

        Vector3 origin = rayOrigin.position;
        Vector3 direction = rayOrigin.forward;

        if (Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, rtSceneMask))
        {
            Debug.DrawLine(origin, hit.point, Color.green);

            if (hitMarker != null)
            {
                hitMarker.position = hit.point;
            }

            if (forceLog || Time.time >= nextLogTime || hit.collider != lastLoggedCollider)
            {
                Debug.Log("RaycastSmokeTest hit: " + hit.collider.name);
                nextLogTime = Time.time + 1f;
                lastLoggedCollider = hit.collider;
            }
        }
        else
        {
            Debug.DrawRay(origin, direction * maxDistance, Color.red);

            if (forceLog)
            {
                Debug.Log("RaycastSmokeTest: no hit.");
            }
        }
    }
}
