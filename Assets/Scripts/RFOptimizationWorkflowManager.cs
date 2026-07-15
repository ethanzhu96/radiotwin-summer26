using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public enum RFOptimizationMode { Router, Reflector }

[DefaultExecutionOrder(60)]
public class RFOptimizationWorkflowManager : MonoBehaviour
{
    public static RFOptimizationWorkflowManager Instance { get; private set; }
    public RFOptimizationMode CurrentMode { get; private set; } = RFOptimizationMode.Router;
    public bool IsMenuOpen { get; private set; }

    private APPlacementManager routerPlacement;
    private ReflectorOptimizationController reflector;
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
    }

    public void HandlePlacePressed()
    {
        Debug.Log("[RFWorkflow] X placement routed to " + CurrentMode + ".");
        if (CurrentMode == RFOptimizationMode.Router) routerPlacement?.HandlePlaceCandidatePressed();
        else reflector?.HandlePlacePressed();
    }

    public void HandleEvaluatePressed()
    {
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
        RectTransform rect = canvas.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(700f, 500f);
        menuRoot.transform.localScale = Vector3.one * .001f;
        menuRaycaster = menuRoot.AddComponent<OVRRaycaster>();
        if (rightController != null) menuRaycaster.pointer = rightController.gameObject;
        Image background = CreateImage(menuRoot.transform, "Background", new Color(.025f,.035f,.055f,.94f), Vector2.zero, new Vector2(700f,500f));
        background.raycastTarget = true;
        CreateText(menuRoot.transform, "RF OPTIMIZATION", new Vector2(0f,205f), new Vector2(620f,55f), 34, FontStyle.Bold);
        routerModeButton = CreateButton(menuRoot.transform, "OPTIMAL ROUTER PLACEMENT", new Vector2(0f,125f));
        reflectorModeButton = CreateButton(menuRoot.transform, "OPTIMAL REFLECTOR PLACEMENT", new Vector2(0f,55f));
        routerModeButton.onClick.AddListener(SelectRouterMode); reflectorModeButton.onClick.AddListener(SelectReflectorMode);
        modeText = CreateText(menuRoot.transform, "", new Vector2(0f,-15f), new Vector2(620f,42f), 25, FontStyle.Bold);
        phaseText = CreateText(menuRoot.transform, "", new Vector2(0f,-62f), new Vector2(620f,36f), 21, FontStyle.Normal);
        summaryText = CreateText(menuRoot.transform, "", new Vector2(0f,-120f), new Vector2(620f,70f), 19, FontStyle.Normal);
        instructionText = CreateText(menuRoot.transform, "", new Vector2(0f,-195f), new Vector2(640f,55f), 17, FontStyle.Normal);
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
        if (rightController == null || menuRoot == null) return;
        Plane menuPlane = new Plane(menuRoot.transform.forward, menuRoot.transform.position);
        Ray ray = new Ray(rightController.position, rightController.forward);
        if (!menuPlane.Raycast(ray, out float distance) || distance > 3f) return;
        Vector3 worldHit = ray.GetPoint(distance);
        Button[] buttons = { routerModeButton, reflectorModeButton };
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
        Material material = new Material(Shader.Find("Sprites/Default")); material.color = new Color(.2f,.8f,1f,.8f);
        uiRay.material=material; uiRay.enabled=false;
    }

    private void UpdateUiRay()
    {
        if (uiRay == null || !uiRay.enabled || rightController == null) return;
        uiRay.SetPosition(0,rightController.position); uiRay.SetPosition(1,rightController.position+rightController.forward*2f);
    }

    private void RefreshUi()
    {
        if (modeText == null) return;
        modeText.text = CurrentMode == RFOptimizationMode.Router ? "Optimal Router Placement" : "Optimal Reflector Placement";
        if (CurrentMode == RFOptimizationMode.Router)
        {
            int count = routerPlacement != null ? routerPlacement.CandidateCount : 0;
            phaseText.text = "Phase: " + (routerPlacement != null ? routerPlacement.State.ToString() : "Waiting");
            APCandidateData winner = routerPlacement != null ? routerPlacement.GetRecommendedCandidate() : null;
            summaryText.text = winner == null ? "Router candidates: " + count + " / 8" :
                "Recommended: " + winner.candidateId + "   Score " + winner.overallScore.ToString("F1") +
                "   Coverage " + winner.coveragePercent.ToString("F0") + "%";
            instructionText.text = "X Place   •   Grip nearby Remove   •   Left stick Evaluate   •   Right stick Heatmap";
        }
        else
        {
            phaseText.text = "Phase: " + (reflector != null ? reflector.PhaseName : "Waiting");
            summaryText.text = reflector == null || !reflector.HasAssumedTx ? "Assumed Tx: Not placed" :
                "Assumed Tx: Ready   •   Reflectors recommended: " + reflector.RecommendationCount;
            instructionText.text = "X Place Assumed Tx   •   Grip nearby Remove   •   Left stick Analyze   •   Right stick Heatmap";
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
    private static Button CreateButton(Transform parent,string label,Vector2 position)
    {
        Image image=CreateImage(parent,label,new Color(.1f,.25f,.38f,.95f),position,new Vector2(570f,54f)); Button button=image.gameObject.AddComponent<Button>();
        ColorBlock colors=button.colors; colors.highlightedColor=new Color(.15f,.55f,.75f,1f); colors.pressedColor=new Color(.08f,.75f,.5f,1f); button.colors=colors;
        CreateText(image.transform,label,Vector2.zero,new Vector2(550f,48f),21,FontStyle.Bold); return button;
    }
    private static void Status(string message) { Debug.Log("[RFWorkflow] " + message); }
}
