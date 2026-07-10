using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

/// <summary>
/// 코드로 통째로 생성하는 uGUI 대시보드 (Unity 2020.3, UI Toolkit 없이).
/// 에디터 수작업 없이 Canvas·패널·텍스트·버튼을 런타임에 만든다.
/// CargoPlacer.OnLayoutChanged 구독 → LoadCalculator로 수치 계산 → 표시.
/// </summary>
public class StaticDashboardUGUI : MonoBehaviour
{
    public CargoPlacer placer;
    public CoGMarkerController cogMarker;
    public SafetyZoneVisualizer safetyZone;

    private Font font;
    private Text lineTotal, lineRisk, lineCoG, lineLtr, lineWheel, lineDrive;
    private readonly List<Button> cargoButtons = new List<Button>();
    private int selectedCard = 0;
    private bool useTons, gridVisible = true, cogVisible = true, physicsOn = false;

    void Start()
    {
        // 2020.3 내장 폰트는 Arial.ttf (LegacyRuntime.ttf는 2022.2+). 한글은 OS 폰트로 폴백.
        font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        EnsureEventSystem();
        BuildCanvas();

        if (placer != null)
        {
            placer.PointerOverUI = IsPointerOverUI;
            placer.OnLayoutChanged += Refresh;
            Refresh(placer.Placed);
        }
    }

    void OnDestroy()
    {
        if (placer != null)
        {
            placer.OnLayoutChanged -= Refresh;
            if (placer.PointerOverUI == IsPointerOverUI) placer.PointerOverUI = null;
        }
    }

    private bool IsPointerOverUI()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    // ── UI 생성 ────────────────────────────────────────────────────────────
    private void BuildCanvas()
    {
        var canvasGO = new GameObject("StaticDashboardCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        // 우측 대시보드 패널
        var panel = MakePanel(canvasGO.transform, new Color(0.06f, 0.07f, 0.10f, 0.95f));
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = new Vector2(1, 0); prt.anchorMax = new Vector2(1, 1);
        prt.pivot = new Vector2(1, 1);
        prt.sizeDelta = new Vector2(420, 0);
        prt.anchoredPosition = Vector2.zero;
        var vlg = panel.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(14, 14, 14, 14);
        vlg.spacing = 8; vlg.childForceExpandHeight = false; vlg.childControlHeight = true; vlg.childControlWidth = true; vlg.childForceExpandWidth = true;

        MakeHeading(panel.transform, "하중 · 무게중심 대시보드");
        lineTotal = MakeLine(panel.transform, 22, new Color(0.5f, 0.75f, 1f));
        lineRisk = MakeLine(panel.transform, 20, Color.green);
        lineCoG = MakeLine(panel.transform, 15, new Color(0.88f, 0.9f, 0.96f));
        lineLtr = MakeLine(panel.transform, 15, new Color(0.88f, 0.9f, 0.96f));
        lineWheel = MakeLine(panel.transform, 15, new Color(0.88f, 0.9f, 0.96f));
        MakeHeading(panel.transform, "주행 위험 예측");
        lineDrive = MakeLine(panel.transform, 16, new Color(0.9f, 0.8f, 0.4f));

        BuildToolbar(canvasGO.transform);
        BuildCargoBar(canvasGO.transform);
    }

    private void BuildToolbar(Transform canvas)
    {
        var bar = MakePanel(canvas, new Color(0.06f, 0.07f, 0.10f, 0.9f));
        var rt = bar.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
        rt.sizeDelta = new Vector2(-420, 46); rt.anchoredPosition = new Vector2(-210, 0);
        var hlg = bar.AddComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(10, 10, 6, 6); hlg.spacing = 6;
        hlg.childForceExpandWidth = false; hlg.childControlWidth = true; hlg.childControlHeight = true; hlg.childAlignment = TextAnchor.MiddleLeft;

        btnGrid = MakeButton(bar.transform, "격자", () => { gridVisible = !gridVisible; placer.SetGridVisible(gridVisible); Tint(btnGrid, gridVisible); });
        btnCog = MakeButton(bar.transform, "CoG", () => { cogVisible = !cogVisible; cogMarker?.SetMarkerVisible(cogVisible); safetyZone?.SetVisible(cogVisible); Tint(btnCog, cogVisible); });
        MakeButton(bar.transform, "시점", () => placer.ToggleCameraView());
        btnPhysics = MakeButton(bar.transform, "물리", () => { physicsOn = !physicsOn; placer.SetPhysics(physicsOn); Tint(btnPhysics, physicsOn); });
        btnSnapGrid = MakeButton(bar.transform, "격자스냅", () => { placer.snapToGrid = !placer.snapToGrid; Tint(btnSnapGrid, placer.snapToGrid); });
        btnSnapMag = MakeButton(bar.transform, "자석", () => { placer.snapToCargo = !placer.snapToCargo; Tint(btnSnapMag, placer.snapToCargo); });
        MakeButton(bar.transform, "되돌리기", () => placer.Undo());
        MakeButton(bar.transform, "전체삭제", () => placer.ClearAll());
        MakeButton(bar.transform, "kg/t", () => { useTons = !useTons; Refresh(placer.Placed); });
        MakeButton(bar.transform, "저장", () => placer.SaveLayout());
        MakeButton(bar.transform, "케이스저장", () => placer.SaveCase());
        MakeButton(bar.transform, "불러오기", () => placer.LoadLayout());

        Tint(btnGrid, gridVisible); Tint(btnCog, cogVisible); Tint(btnPhysics, physicsOn);
        Tint(btnSnapGrid, placer.snapToGrid); Tint(btnSnapMag, placer.snapToCargo);
    }

    private Button btnGrid, btnCog, btnPhysics, btnSnapGrid, btnSnapMag;

    /// <summary>토글 버튼 켜짐/꺼짐 색. 특히 물리 ON 상태가 한눈에 보이게.</summary>
    private static void Tint(Button b, bool on)
    {
        if (b != null)
            b.GetComponent<Image>().color = on
                ? new Color(0.23f, 0.47f, 0.9f, 1f)
                : new Color(0.13f, 0.15f, 0.2f, 1f);
    }

    private void BuildCargoBar(Transform canvas)
    {
        var bar = MakePanel(canvas, new Color(0.06f, 0.07f, 0.10f, 0.9f));
        var rt = bar.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 0); rt.pivot = new Vector2(0.5f, 0);
        rt.sizeDelta = new Vector2(-420, 78); rt.anchoredPosition = new Vector2(-210, 0);

        // 화물 종류가 많아(17종) 마우스 휠로 가로 스크롤
        var scroll = bar.AddComponent<ScrollRect>();
        scroll.horizontal = true; scroll.vertical = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 25f;

        var viewport = MakePanel(bar.transform, new Color(0f, 0f, 0f, 0f));
        var vrt = viewport.GetComponent<RectTransform>();
        vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
        vrt.offsetMin = Vector2.zero; vrt.offsetMax = Vector2.zero;
        viewport.AddComponent<RectMask2D>();

        var content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(viewport.transform, false);
        var crt = content.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0, 0); crt.anchorMax = new Vector2(0, 1); crt.pivot = new Vector2(0, 0.5f);
        crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
        var hlg = content.AddComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(10, 10, 8, 8); hlg.spacing = 8;
        hlg.childForceExpandWidth = false; hlg.childControlWidth = true; hlg.childControlHeight = true; hlg.childAlignment = TextAnchor.MiddleLeft;
        var fitter = content.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = vrt;
        scroll.content = crt;

        cargoButtons.Clear();
        if (placer?.cargoTypes == null) return;
        for (int i = 0; i < placer.cargoTypes.Length; i++)
        {
            CargoType t = placer.cargoTypes[i];
            int idx = i;
            string stock = t.stockCount > 0 ? $" · 재고 {t.stockCount}" : "";
            Button b = MakeButton(content.transform, $"{t.name}\n{FormatMassShort(t.massKg)}{stock}", () => SelectCard(idx));
            var le = b.GetComponent<LayoutElement>();
            le.preferredWidth = 132; le.preferredHeight = 56;
            var txt = b.GetComponentInChildren<Text>();
            if (txt != null) txt.fontSize = 12;
            cargoButtons.Add(b);
        }
        SelectCard(0);
    }

    private void SelectCard(int index)
    {
        if (cargoButtons.Count == 0) return;
        selectedCard = Mathf.Clamp(index, 0, cargoButtons.Count - 1);
        for (int i = 0; i < cargoButtons.Count; i++)
        {
            var img = cargoButtons[i].GetComponent<Image>();
            img.color = i == selectedCard ? new Color(0.23f, 0.47f, 0.9f, 1f) : new Color(0.13f, 0.15f, 0.2f, 1f);
        }
        placer?.SetActiveType(selectedCard);
    }

    // ── 값 갱신 ────────────────────────────────────────────────────────────
    private void Refresh(IReadOnlyList<PlacedCargo> cargo)
    {
        if (placer == null) return;
        float empty = placer.EmptyMassKg;
        Vector3 emptyCoG = new Vector3(placer.TrayCenterX, (placer.transform.position.y + placer.BedTopY) * 0.5f, placer.TrayCenterZ); // 빈 적재함 CoG = 트레이 중심
        float total = LoadCalculator.ComputeTotalMass(cargo, empty);
        Vector3 cog = LoadCalculator.ComputeCoG(cargo, empty, emptyCoG);
        FourPointLoad load = LoadCalculator.ComputeLoads(cog, total, placer.WorldSupports);
        float ltr = LoadCalculator.ComputeLTR(load);
        RiskResult risk = LoadCalculator.ComputeRisk(ltr, placer.Thresholds);

        float marginSafe = safetyZone != null ? safetyZone.marginSafe : 0.03f;
        float margin = total > 0f ? LoadCalculator.StabilityMargin(new Vector2(cog.x, cog.z), placer.WorldSupports) : marginSafe;
        if (margin < 0f) risk = new RiskResult { Level = RiskLevel.Danger, Grade = 3, Label = "전복" };

        lineTotal.text = $"총중량  {FormatMass(total)}";
        lineRisk.text = $"위험도  {risk.Label}   (LTR {ltr:0.00})";
        lineRisk.color = RiskColor(risk.Level);

        float cx = cog.x - placer.transform.position.x, cz = cog.z - placer.transform.position.z, cy = total > 0 ? cog.y - placer.BedTopY : 0f;
        // X=좌우, Y=높이, Z=전후 (트레이 로컬, m). cm도 병기.
        lineCoG.text = $"CoG  X {cx:0.000}  Y {cy:0.000}  Z {cz:0.000} m   " +
                       $"({cx * 100f:0.0}, {cy * 100f:0.0}, {cz * 100f:0.0} cm)";
        lineLtr.text = $"축하중  전 {FormatMass(load.Front)}   후 {FormatMass(load.Rear)}";
        lineWheel.text = $"4륜  FL {FormatMass(load.FL)}  FR {FormatMass(load.FR)}  RL {FormatMass(load.RL)}  RR {FormatMass(load.RR)}";

        // 주행 위험 예측
        int count = cargo?.Count ?? 0;
        float mass = 0f, unsec = 0f; int overflow = 0;
        bool walls = placer.HasWalls; float wallTop = placer.WallTopY;
        if (cargo != null) foreach (var p in cargo)
        {
            if (p == null || p.type == null) continue;
            mass += p.type.massKg; if (!p.secured) unsec += p.type.massKg;
            if (walls && p.worldPos.y > wallTop) overflow++;
        }
        float unsecFrac = mass > 0 ? unsec / mass : 0f;
        float cogH = count > 0 ? Mathf.Max(0f, cog.y - placer.BedTopY) : 0f;
        float ssf = count > 0 ? LoadCalculator.LateralTipG(placer.WorldSupports.HalfTrack, cogH) : 999f;
        RiskLevel dl; string dlabel;
        if (count == 0) { dl = RiskLevel.Safe; dlabel = "주행 안정"; }
        else if (overflow > 0 || unsecFrac > 0.7f || ssf < 0.8f) { dl = RiskLevel.Danger; dlabel = "주행 위험"; }
        else if (unsecFrac > 0.3f || ssf < 1.5f) { dl = RiskLevel.Caution; dlabel = "주행 주의"; }
        else { dl = RiskLevel.Safe; dlabel = "주행 안정"; }
        lineDrive.text = $"{dlabel}   미고정 {Mathf.RoundToInt(unsecFrac * 100)}%  넘침 {overflow}  전복임계 {(count == 0 ? "-" : (ssf >= 9 ? "9+" : ssf.ToString("0.0")))}g";
        lineDrive.color = RiskColor(dl);
    }

    // 목업 화물은 그램 단위라 1 kg 미만은 g로 표시 (반올림하면 전부 0 kg가 됨)
    private string FormatMass(float kg)
    {
        if (useTons) return $"{kg / 1000f:0.0000} t";
        if (kg < 1f) return $"{kg * 1000f:0} g";
        return $"{kg:0.00} kg";
    }

    private static string FormatMassShort(float kg) => kg < 1f ? $"{kg * 1000f:0}g" : $"{kg:0.00}kg";
    private static Color RiskColor(RiskLevel l) => l == RiskLevel.Danger ? new Color(1f, 0.37f, 0.37f) : l == RiskLevel.Caution ? new Color(0.96f, 0.78f, 0.31f) : new Color(0.29f, 0.87f, 0.54f);

    // ── uGUI 헬퍼 ──────────────────────────────────────────────────────────
    private GameObject MakePanel(Transform parent, Color bg)
    {
        var go = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = bg;
        return go;
    }

    private Text MakeLine(Transform parent, int size, Color color)
    {
        var go = new GameObject("Line", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = font; t.fontSize = size; t.color = color; t.text = "";
        t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
        var le = go.AddComponent<LayoutElement>(); le.minHeight = size + 8;
        return t;
    }

    private void MakeHeading(Transform parent, string text)
    {
        var t = MakeLine(parent, 13, new Color(0.6f, 0.64f, 0.72f));
        t.text = text; t.fontStyle = FontStyle.Bold;
    }

    private Button MakeButton(Transform parent, string label, Action onClick)
    {
        var go = new GameObject("Btn", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = new Color(0.13f, 0.15f, 0.2f, 1f);
        var le = go.GetComponent<LayoutElement>(); le.preferredHeight = 30; le.preferredWidth = 78;
        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(() => onClick());

        var txtGO = new GameObject("Text", typeof(RectTransform));
        txtGO.transform.SetParent(go.transform, false);
        var rt = txtGO.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        var t = txtGO.AddComponent<Text>();
        t.font = font; t.text = label; t.fontSize = 13; t.color = Color.white; t.alignment = TextAnchor.MiddleCenter;
        return btn;
    }

    private void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
    }
}
