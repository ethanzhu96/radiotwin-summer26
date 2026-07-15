using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public enum RFOptimizationMode { Router, Reflector }
public enum PropagationBackend { Simple, Sionna }

[DefaultExecutionOrder(60)]
public class RFOptimizationWorkflowManager : MonoBehaviour
{
    public static RFOptimizationWorkflowManager Instance { get; private set; }
    public RFOptimizationMode CurrentMode { get; private set; } = RFOptimizationMode.Router;
    public PropagationBackend CurrentBackend { get; private set; } = PropagationBackend.Simple;
    public bool IsMenuOpen { get; private set; }

    private static readonly Color ButtonNormalColor = new Color(.1f, .25f, .38f, .95f);
    private static readonly Color ButtonSelectedColor = new Color(.05f, .62f, .72f, 1f);
    private static readonly Color RayColor = new Color(.2f, .8f, 1f, .8f);
    private static readonly Color RayHitColor = new Color(.2f, 1f, .55f, 1f);

    private APPlacementManager routerPlacement;
    private ReflectorOptimizationController reflector;
    private RadioLensPropagationController propagation;
    private Transform centerEye;
    private Transform rightController;
    private Canvas canvas;
    private OVRRaycaster menuRaycaster;
    private EventSystem menuEventSystem;
    private GameObject menuRoot;
    private LineRenderer uiRay;
    private Text modeText;
    private Text phaseText;
    private Text summaryText;
    private Text instructionText;
    private Button routerModeButton;
    private Button reflectorModeButton;
    private Button simpleBackendButton;
    private Button sionnaBackendButton;
    private Text backendStatusText;
    private float nextUiRefresh;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindFirstObjectByType<RFOptimizationWorkflowManager>() == null)
            new GameObject("RFOptimizationSystem").AddComponent<RFOptimizationWorkflowManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        ResolveReferences();
        BuildMenu();
        SelectRouterMode();
        SelectSimpleBackend();
        SetMenuOpen(false);
    }

    private void Update()
    {
        ResolveReferences();
        if (OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
        {
            SetMenuOpen(!IsMenuOpen);
        }
        if (!IsMenuOpen)
        {
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) HandlePlacePressed();
            if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch)) HandleEvaluatePressed();
        }
        else if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
        {
            ClickMenuWithRightController();
        }
        UpdateUiRay();
        if (Time.unscaledTime >= nextUiRefresh) { nextUiRefresh = Time.unscaledTime + .25f; RefreshUi(); }
    }

    private void ResolveReferences()
    {
        if (routerPlacement == null)
        {
            routerPlacement = FindFirstObjectByType<APPlacementManager>();
            if (routerPlacement != null) routerPlacement.InputEnabled = false;
        }
        OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            if (centerEye == null) centerEye = rig.centerEyeAnchor;
            if (rightController == null) rightController = rig.rightControllerInHandAnchor;
        }
        if (reflector == null)
        {
            reflector = GetComponent<ReflectorOptimizationController>();
            if (reflector == null) reflector = gameObject.AddComponent<ReflectorOptimizationController>();
        }
        if (reflector != null && rig != null)
            reflector.Initialize(rig.leftControllerInHandAnchor);
        if (propagation == null) propagation = FindFirstObjectByType<RadioLensPropagationController>();
        if (propagation == null) propagation = gameObject.AddComponent<RadioLensPropagationController>();
        if (menuRaycaster != null && rightController != null && menuRaycaster.pointer != rightController.gameObject)
            menuRaycaster.pointer = rightController.gameObject;
    }

    public void HandlePlacePressed()
    {
        Debug.Log("[RFWorkflow] X placement routed to " + CurrentMode + ".");
        if (CurrentMode == RFOptimizationMode.Router) routerPlacement?.HandlePlaceCandidatePressed();
        else reflector?.HandlePlacePressed();
    }

    public void HandleEvaluatePressed()
    {
        if (CurrentBackend == PropagationBackend.Sionna)
            propagation?.ReportSimpleRecommendationFallback();
        Debug.Log("[RFWorkflow] Evaluation routed to " + CurrentMode + ".");
        if (CurrentMode == RFOptimizationMode.Router) routerPlacement?.RequestEvaluation();
        else reflector?.RequestAnalysis();
    }

    public void SelectRouterMode()
    {
        if (AnyEvaluationRunning()) { Status("Wait for the current evaluation to finish."); return; }
        CurrentMode = RFOptimizationMode.Router;
        if (routerPlacement != null) { routerPlacement.SetVisualsVisible(true); routerPlacement.RestoreActiveHeatmap(); }
        if (reflector != null) { reflector.Active = false; reflector.SetVisualsVisible(false); }
        Status("Router mode active.");
        RefreshUi();
    }

    public void SelectReflectorMode()
    {
        if (AnyEvaluationRunning()) { Status("Wait for the current evaluation to finish."); return; }
        CurrentMode = RFOptimizationMode.Reflector;
        if (routerPlacement != null) routerPlacement.SetVisualsVisible(false);
        if (reflector != null) { reflector.Active = true; reflector.SetVisualsVisible(true); reflector.RestoreHeatmap(); }
        Status("Reflector mode active.");
        RefreshUi();
    }

    public void SelectSimpleBackend()
    {
        CurrentBackend = PropagationBackend.Simple;
        ResolveReferences();
        propagation?.SelectBackend(CurrentBackend);
        RefreshUi();
    }

    public void SelectSionnaBackend()
    {
        CurrentBackend = PropagationBackend.Sionna;
        ResolveReferences();
        propagation?.SelectBackend(CurrentBackend);
        RefreshUi();
    }

    public void SetPropagationStatus(string message)
    {
        if (backendStatusText != null) backendStatusText.text = message;
    }

    private bool AnyEvaluationRunning() => (routerPlacement != null && routerPlacement.IsEvaluating) ||
        (reflector != null && reflector.IsEvaluating);

    private void SetMenuOpen(bool open)
    {
        IsMenuOpen = open;
        if (menuRoot != null) menuRoot.SetActive(open);
        if (uiRay != null) uiRay.enabled = open;
        if (open && centerEye != null && menuRoot != null)
        {
            Vector3 flatForward = Vector3.ProjectOnPlane(centerEye.forward, Vector3.up).normalized;
            if (flatForward.sqrMagnitude < .01f) flatForward = Vector3.forward;
            menuRoot.transform.position = centerEye.position + flatForward * .9f - Vector3.up * .12f;
            menuRoot.transform.rotation = Quaternion.LookRotation(flatForward, Vector3.up);
        }
        Debug.Log("[RFOptimizationUI] Menu " + (open ? "opened" : "closed") + ".");
    }

    private void BuildMenu()
    {
        menuRoot = new GameObject("RFOptimizationCanvas");
        canvas = menuRoot.AddComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        RectTransform rect = canvas.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(700f, 650f);
        menuRoot.transform.localScale = Vector3.one * .001f;
        menuRaycaster = menuRoot.AddComponent<OVRRaycaster>();
        if (rightController != null) menuRaycaster.pointer = rightController.gameObject;
        Image background = CreateImage(menuRoot.transform, "Background", new Color(.025f,.035f,.055f,.94f), Vector2.zero, new Vector2(700f,650f));
        background.raycastTarget = true;
        CreateText(menuRoot.transform, "RF OPTIMIZATION", new Vector2(0f,280f), new Vector2(620f,55f), 34, FontStyle.Bold);

        CreateText(menuRoot.transform, "GUIDANCE MODE", new Vector2(0f,225f), new Vector2(620f,34f), 19, FontStyle.Bold);
        routerModeButton = CreateButton(menuRoot.transform, "ROUTER PLACEMENT", new Vector2(-150f,175f), new Vector2(280f,54f));
        reflectorModeButton = CreateButton(menuRoot.transform, "REFLECTOR PLACEMENT", new Vector2(150f,175f), new Vector2(280f,54f));
        routerModeButton.onClick.AddListener(SelectRouterMode); reflectorModeButton.onClick.AddListener(SelectReflectorMode);

        CreateText(menuRoot.transform, "PROPAGATION MODEL", new Vector2(0f,110f), new Vector2(620f,34f), 19, FontStyle.Bold);
        simpleBackendButton = CreateButton(menuRoot.transform, "SIMPLE", new Vector2(-150f,60f), new Vector2(280f,54f));
        sionnaBackendButton = CreateButton(menuRoot.transform, "SIONNA RT", new Vector2(150f,60f), new Vector2(280f,54f));
        simpleBackendButton.onClick.AddListener(SelectSimpleBackend); sionnaBackendButton.onClick.AddListener(SelectSionnaBackend);
        backendStatusText = CreateText(menuRoot.transform, "", new Vector2(0f,18f), new Vector2(620f,30f), 16, FontStyle.Italic);

        modeText = CreateText(menuRoot.transform, "", new Vector2(0f,-35f), new Vector2(620f,38f), 23, FontStyle.Bold);
        phaseText = CreateText(menuRoot.transform, "", new Vector2(0f,-78f), new Vector2(620f,34f), 20, FontStyle.Normal);
        summaryText = CreateText(menuRoot.transform, "", new Vector2(0f,-138f), new Vector2(620f,70f), 18, FontStyle.Normal);
        instructionText = CreateText(menuRoot.transform, "", new Vector2(0f,-245f), new Vector2(640f,70f), 16, FontStyle.Normal);
        EnsureEventSystem();
        CreateUiRay();
    }

    private void EnsureEventSystem()
    {
        EventSystem system = FindFirstObjectByType<EventSystem>();
        if (system == null) system = new GameObject("RFOptimizationEventSystem").AddComponent<EventSystem>();
        menuEventSystem = system;
        OVRInputModule module = system.GetComponent<OVRInputModule>();
        if (module == null) module = system.gameObject.AddComponent<OVRInputModule>();
        module.rayTransform = rightController;
        module.joyPadClickButton = OVRInput.Button.None;
    }

    private void ClickMenuWithRightController()
    {
        if (!TryGetMenuHit(out Vector3 worldHit, out _)) return;
        Button[] buttons = { routerModeButton, reflectorModeButton, simpleBackendButton, sionnaBackendButton };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] == null) continue;
            RectTransform rect = buttons[i].GetComponent<RectTransform>();
            Vector3 localHit = rect.InverseTransformPoint(worldHit);
            if (rect.rect.Contains(new Vector2(localHit.x, localHit.y)))
            {
                buttons[i].onClick.Invoke();
                Debug.Log("[RFOptimizationUI] Right-trigger click: " + buttons[i].gameObject.name + ".");
                return;
            }
        }
    }

    private void CreateUiRay()
    {
        GameObject rayObject = new GameObject("RFOptimizationUIRay");
        uiRay = rayObject.AddComponent<LineRenderer>(); uiRay.positionCount=2; uiRay.useWorldSpace=true;
        uiRay.startWidth=.004f; uiRay.endWidth=.002f;
        Material material = new Material(Shader.Find("Sprites/Default")); material.color = RayColor;
        uiRay.material=material; uiRay.enabled=false;
    }

    private void UpdateUiRay()
    {
        if (uiRay == null || !uiRay.enabled || rightController == null) return;
        bool hitMenu = TryGetMenuHit(out Vector3 hitPoint, out _);
        uiRay.SetPosition(0, rightController.position);
        uiRay.SetPosition(1, hitMenu ? hitPoint : rightController.position + rightController.forward * 2f);
        uiRay.material.color = hitMenu ? RayHitColor : RayColor;
    }

    private bool TryGetMenuHit(out Vector3 worldHit, out float distance)
    {
        worldHit = default;
        distance = 0f;
        if (!IsMenuOpen || rightController == null || menuRoot == null) return false;
        Ray ray = new Ray(rightController.position, rightController.forward);
        Plane menuPlane = new Plane(menuRoot.transform.forward, menuRoot.transform.position);
        if (!menuPlane.Raycast(ray, out distance) || distance < 0f || distance > 3f) return false;
        worldHit = ray.GetPoint(distance);
        RectTransform canvasRect = canvas != null ? canvas.GetComponent<RectTransform>() : null;
        if (canvasRect == null) return false;
        Vector3 localHit = canvasRect.InverseTransformPoint(worldHit);
        return canvasRect.rect.Contains(new Vector2(localHit.x, localHit.y));
    }

    private void RefreshUi()
    {
        if (modeText == null) return;
        SetButtonSelected(routerModeButton, CurrentMode == RFOptimizationMode.Router);
        SetButtonSelected(reflectorModeButton, CurrentMode == RFOptimizationMode.Reflector);
        SetButtonSelected(simpleBackendButton, CurrentBackend == PropagationBackend.Simple);
        SetButtonSelected(sionnaBackendButton, CurrentBackend == PropagationBackend.Sionna);
        if (backendStatusText != null && propagation != null)
            backendStatusText.text = propagation.CurrentStatus;
        modeText.text = CurrentMode == RFOptimizationMode.Router ? "Optimal Router Placement" : "Optimal Reflector Placement";
        if (CurrentMode == RFOptimizationMode.Router)
        {
            int count = routerPlacement != null ? routerPlacement.CandidateCount : 0;
            phaseText.text = "Phase: " + (routerPlacement != null ? routerPlacement.State.ToString() : "Waiting");
            APCandidateData winner = routerPlacement != null ? routerPlacement.GetRecommendedCandidate() : null;
            summaryText.text = winner == null ? "Router candidates: " + count + " / 8" :
                "Recommended: " + winner.candidateId + "   Score " + winner.overallScore.ToString("F1") +
                "   Coverage " + winner.coveragePercent.ToString("F0") + "%";
            instructionText.text = CurrentBackend == PropagationBackend.Sionna
                ? "X Place Router   •   Right trigger Select Rx   •   Right grip Trace/Clear   •   Left stick Recommend"
                : "X Place   •   Grip nearby Remove   •   Left stick Evaluate   •   Right stick Heatmap";
        }
        else
        {
            phaseText.text = "Phase: " + (reflector != null ? reflector.PhaseName : "Waiting");
            summaryText.text = reflector == null || !reflector.HasAssumedTx ? "Assumed Tx: Not placed" :
                "Assumed Tx: Ready   •   Reflectors recommended: " + reflector.RecommendationCount;
            instructionText.text = CurrentBackend == PropagationBackend.Sionna
                ? "X Place Tx   •   Right trigger Select Rx   •   Right grip Trace/Clear   •   Left stick Recommend"
                : "X Place Assumed Tx   •   Grip nearby Remove   •   Left stick Analyze   •   Right stick Heatmap";
        }
    }

    private static Image CreateImage(Transform parent,string name,Color color,Vector2 position,Vector2 size)
    {
        GameObject obj=new GameObject(name); obj.transform.SetParent(parent,false); Image image=obj.AddComponent<Image>(); image.color=color;
        RectTransform rect=obj.GetComponent<RectTransform>(); rect.anchoredPosition=position; rect.sizeDelta=size; return image;
    }
    private static Text CreateText(Transform parent,string value,Vector2 position,Vector2 size,int fontSize,FontStyle style)
    {
        GameObject obj=new GameObject("Text"); obj.transform.SetParent(parent,false); Text text=obj.AddComponent<Text>();
        text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.text=value; text.fontSize=fontSize;
        text.fontStyle=style; text.alignment=TextAnchor.MiddleCenter; text.color=Color.white; text.raycastTarget=false;
        RectTransform rect=obj.GetComponent<RectTransform>(); rect.anchoredPosition=position; rect.sizeDelta=size; return text;
    }
    private static Button CreateButton(Transform parent,string label,Vector2 position,Vector2 size)
    {
        Image image=CreateImage(parent,label,ButtonNormalColor,position,size); Button button=image.gameObject.AddComponent<Button>();
        ColorBlock colors=button.colors; colors.highlightedColor=new Color(.15f,.55f,.75f,1f); colors.pressedColor=new Color(.08f,.75f,.5f,1f); button.colors=colors;
        CreateText(image.transform,label,Vector2.zero,size-new Vector2(18f,6f),18,FontStyle.Bold); return button;
    }

    private static void SetButtonSelected(Button button, bool selected)
    {
        if (button == null) return;
        Image image = button.GetComponent<Image>();
        if (image != null) image.color = selected ? ButtonSelectedColor : ButtonNormalColor;
    }
    private static void Status(string message) { Debug.Log("[RFWorkflow] " + message); }
}
