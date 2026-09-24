using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mod6_NavCompass
{
    // Roza wiatrow na HUD-zie pokazujaca kierunek do recznie oznaczonych pinow na mapie.
    //
    // Klikniecie pinu na mapie przelacza jego stan (wlasny system, niezalezny od wbudowanego
    // PinData.m_checked - gra ma juz swoj wlasny wizualny "checked" stan, wiec nie dublujemy go):
    //   None -> Circled (cienki jasnoniebieski pierscien) -> None
    // Tylko piny w stanie Circled sa pokazywane na kompasie na gorze ekranu.
    // Oznaczenia sa zapisywane do pliku per-swiat (BepInEx/plugins/Mod6-NavCompass/tracked_<swiat>.json),
    // wiec przetrwaja restart gry.
    //
    // Wykrywanie klikietego pinu: wlasna wersja Minimap.GetClosestPin (ta z gry wymaga
    // pin.m_save==true, wiec piny niezapisywalne typu sklep/trader nigdy by nie zadzialaly).
    //
    // To NIE jest auto-pathfinding - tylko wizualna wskazowka kierunku i dystansu.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class NavCompassPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.michal.valheim.navcompass";
        public const string PluginName = "Nav Compass";
        public const string PluginVersion = "0.1.0";

        private const float StripWidth = 480f;
        private const float StripHeight = 22f;
        private const float StripHalfFovDeg = 90f;
        private const float PositionMatchTolerance = 1f;

        private static readonly FieldInfo PinsField = AccessTools.Field(typeof(Minimap), "m_pins");
        private static readonly MethodInfo ScreenToWorldPointMethod = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint");
        private static readonly PropertyInfo PinInteractRadiusProp = AccessTools.Property(typeof(Minimap), "PinInteractRadius");

        internal static ManualLogSource Log;
        private static NavCompassPlugin _instance;

        private enum MarkState { None, Strikethrough, Circled }

        private readonly Dictionary<Minimap.PinData, MarkState> _pinStates = new Dictionary<Minimap.PinData, MarkState>();
        private readonly Dictionary<Minimap.PinData, GameObject> _overlayByPin = new Dictionary<Minimap.PinData, GameObject>();
        private readonly Dictionary<Minimap.PinData, PinMarker> _compassMarkers = new Dictionary<Minimap.PinData, PinMarker>();

        private GameObject _rootGo;
        private RectTransform _stripRect;
        private readonly Dictionary<string, RectTransform> _cardinalMarkers = new Dictionary<string, RectTransform>();
        private TMP_FontAsset _font;
        private Sprite _ringSprite;
        private bool _uiBuilt;
        private bool _faulted;
        private bool _savedStateLoaded;
        private string _currentWorldName;

        private class PinMarker
        {
            public RectTransform Root;
            public TextMeshProUGUI DistanceText;
        }

        [Serializable]
        private class TrackedPinsSaveData
        {
            public List<Vector3> positions = new List<Vector3>();
        }

        private void Awake()
        {
            Log = Logger;
            _instance = this;
            new Harmony(PluginGUID).PatchAll(typeof(NavCompassPlugin).Assembly);
        }

        private void Update()
        {
            if (_faulted)
                return;

            try
            {
                RunUpdate();
            }
            catch (Exception e)
            {
                _faulted = true;
                if (_rootGo != null) _rootGo.SetActive(false);
                Log.LogError($"Nav Compass wylaczony po nieoczekiwanym bledzie (zeby nie zapychac logu/FPS): {e}");
            }
        }

        private void RunUpdate()
        {
            if (Player.m_localPlayer == null || ZNet.instance == null)
            {
                if (_rootGo != null) _rootGo.SetActive(false);
                _savedStateLoaded = false;
                return;
            }

            if (!_uiBuilt)
            {
                _ringSprite = CreateRingSprite();
                BuildUi();
                _uiBuilt = true;
            }

            if (!_savedStateLoaded)
            {
                TryLoadTrackedPins();
            }

            if (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large)
                ResyncOverlays();

            UpdateCompass();
        }

        private TMP_FontAsset GetFont()
        {
            if (_font != null)
                return _font;

            var live = FindObjectOfType<TextMeshProUGUI>();
            if (live != null && live.font != null)
            {
                _font = live.font;
                return _font;
            }

            if (TMP_Settings.defaultFontAsset != null)
            {
                _font = TMP_Settings.defaultFontAsset;
                return _font;
            }

            _font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault();
            return _font;
        }

        // ---------- oznaczanie pinow (klik na mapie) ----------

        // Wlasna wersja Minimap.GetClosestPin - ta z gry wymaga pin.m_save==true, przez co
        // piny niezapisywalne (sklepy/trader itp.) nigdy nie zostaja wykryte jako kliknieta.
        // My chcemy moc oznaczac KAZDY widoczny pin, niezaleznie od m_save.
        private static Minimap.PinData FindClosestPinAnySave(Vector3 pos, float radius)
        {
            var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
            if (pins == null)
                return null;

            Minimap.PinData best = null;
            float bestDist = radius;
            foreach (var pin in pins)
            {
                if (pin.m_uiElement == null || !pin.m_uiElement.gameObject.activeInHierarchy)
                    continue;

                float dx = pos.x - pin.m_pos.x;
                float dz = pos.z - pin.m_pos.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < bestDist)
                {
                    best = pin;
                    bestDist = d;
                }
            }
            return best;
        }

        [HarmonyPatch(typeof(Minimap), "OnMapLeftClick")]
        private static class Minimap_OnMapLeftClick_Patch
        {
            private static void Postfix(Minimap __instance)
            {
                try
                {
                    Vector3 mousePos = ZInput.pointerPosition;
                    Vector3 worldPos = (Vector3)ScreenToWorldPointMethod.Invoke(__instance, new object[] { mousePos });
                    float radius = (float)PinInteractRadiusProp.GetValue(__instance);
                    var closestPin = FindClosestPinAnySave(worldPos, radius);
                    if (closestPin != null)
                        _instance?.CycleState(closestPin);
                }
                catch (Exception e)
                {
                    Log.LogError($"Blad przy wykrywaniu kliknietego pinu: {e}");
                }
            }
        }

        // Gra sama przelacza pin.m_checked (czerwony "przekreslony" wyglad) przy kazdym kliknieciu
        // na pin, niezaleznie od nas - dlatego jawnie nadpisujemy ta wartosc zeby dopasowac ja
        // do WLASNEGO 3-stanowego licznika: None -> Strikethrough -> Circled -> None.
        private void CycleState(Minimap.PinData pin)
        {
            _pinStates.TryGetValue(pin, out var current);
            var next = (MarkState)(((int)current + 1) % 3);

            if (next == MarkState.None)
                _pinStates.Remove(pin);
            else
                _pinStates[pin] = next;

            pin.m_checked = next == MarkState.Strikethrough;

            if (_overlayByPin.TryGetValue(pin, out var oldOverlay))
            {
                if (oldOverlay != null) Destroy(oldOverlay);
                _overlayByPin.Remove(pin);
            }

            if (next == MarkState.Circled)
            {
                var overlay = CreateRingOverlay(pin);
                if (overlay != null) _overlayByPin[pin] = overlay;
            }

            Log.LogInfo($"Pin '{pin.m_name}': {next}");
            SaveTrackedPins();
        }

        private GameObject CreateRingOverlay(Minimap.PinData pin)
        {
            if (pin.m_uiElement == null || _ringSprite == null)
                return null;

            var go = new GameObject("MarkRing", typeof(RectTransform));
            go.transform.SetParent(pin.m_uiElement, false);

            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            img.sprite = _ringSprite;
            img.color = new Color(0.45f, 0.8f, 1f, 0.95f); // jasny niebieski

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(36f, 36f);

            go.transform.SetAsFirstSibling();
            return go;
        }

        private IEnumerable<Minimap.PinData> CircledPins => _pinStates.Where(kv => kv.Value == MarkState.Circled).Select(kv => kv.Key);

        // jesli gracz usunie oznaczony pin z mapy (prawy klik -> usun), trzeba go tez
        // sciagnac z naszego stanu, inaczej zostaje na kompasie na zawsze i nie da sie go
        // juz odznaczyc (bo nie da sie go kliknac, skoro zniknal z mapy).
        private void RemoveDeletedPins()
        {
            if (_pinStates.Count == 0)
                return;

            var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
            var alive = pins != null ? new HashSet<Minimap.PinData>(pins) : new HashSet<Minimap.PinData>();

            bool anyRemoved = false;
            foreach (var stale in _pinStates.Keys.Where(p => !alive.Contains(p)).ToList())
            {
                _pinStates.Remove(stale);
                if (_overlayByPin.TryGetValue(stale, out var overlay))
                {
                    if (overlay != null) Destroy(overlay);
                    _overlayByPin.Remove(stale);
                }
                anyRemoved = true;
            }

            if (anyRemoved)
                SaveTrackedPins();
        }

        // dopina/odtwarza pierscienie, jesli gra w miedzyczasie przebudowala UI pinow na mapie
        private void ResyncOverlays()
        {
            foreach (var pin in CircledPins.ToList())
            {
                bool needsOverlay = !_overlayByPin.TryGetValue(pin, out var overlay) || overlay == null ||
                                     overlay.transform.parent != pin.m_uiElement;
                if (needsOverlay)
                {
                    if (overlay != null) Destroy(overlay);
                    _overlayByPin.Remove(pin);
                    var newOverlay = CreateRingOverlay(pin);
                    if (newOverlay != null) _overlayByPin[pin] = newOverlay;
                }
            }
        }

        private static Sprite CreateRingSprite()
        {
            const int size = 64;
            const float outerR = 28f;
            const float innerR = 25f; // cienki pierscien (~3px)
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = new Vector2(size / 2f, size / 2f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = (d <= outerR && d >= innerR) ? 1f : 0f;
                    if (d > outerR - 1f && d < outerR + 1f) alpha = Mathf.Clamp01(outerR - d + 0.5f);
                    if (d > innerR - 1f && d < innerR + 1f) alpha = Mathf.Clamp01(d - innerR + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        // ---------- zapis / odczyt oznaczen (per swiat) ----------

        private static string SaveDir => Path.GetDirectoryName(typeof(NavCompassPlugin).Assembly.Location);

        private static string SaveFilePath(string worldName)
        {
            string safe = string.Join("_", worldName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(SaveDir, $"tracked_{safe}.json");
        }

        private void SaveTrackedPins()
        {
            try
            {
                string worldName = ZNet.instance?.GetWorldName();
                if (string.IsNullOrEmpty(worldName))
                    return;

                var data = new TrackedPinsSaveData { positions = CircledPins.Select(p => p.m_pos).ToList() };
                File.WriteAllText(SaveFilePath(worldName), JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie zapisac oznaczonych pinow: {e}");
            }
        }

        private void TryLoadTrackedPins()
        {
            string worldName = ZNet.instance?.GetWorldName();
            if (string.IsNullOrEmpty(worldName))
                return;

            _currentWorldName = worldName;
            _savedStateLoaded = true;

            string path = SaveFilePath(worldName);
            if (!File.Exists(path))
                return;

            try
            {
                var data = JsonUtility.FromJson<TrackedPinsSaveData>(File.ReadAllText(path));
                var pins = PinsField.GetValue(Minimap.instance) as List<Minimap.PinData>;
                if (data?.positions == null || pins == null)
                    return;

                int loaded = 0;
                foreach (var savedPos in data.positions)
                {
                    var match = pins.FirstOrDefault(p => Vector3.Distance(p.m_pos, savedPos) < PositionMatchTolerance);
                    if (match != null && (!_pinStates.TryGetValue(match, out var st) || st != MarkState.Circled))
                    {
                        _pinStates[match] = MarkState.Circled;
                        match.m_checked = false; // Circled = bez wbudowanego przekreslenia
                        var overlay = CreateRingOverlay(match);
                        if (overlay != null) _overlayByPin[match] = overlay;
                        loaded++;
                    }
                }

                Log.LogInfo($"Wczytano {loaded} oznaczonych pinow dla swiata '{worldName}'.");
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie wczytac oznaczonych pinow: {e}");
            }
        }

        // ---------- kompas na HUD ----------

        private void BuildUi()
        {
            _rootGo = new GameObject("NavCompassRose");
            DontDestroyOnLoad(_rootGo);

            var canvas = _rootGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = _rootGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            _rootGo.AddComponent<GraphicRaycaster>();

            var bgGo = new GameObject("StripBg");
            bgGo.transform.SetParent(_rootGo.transform, false);
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0.25f);
            _stripRect = bgGo.GetComponent<RectTransform>();
            _stripRect.anchorMin = new Vector2(0.5f, 1f);
            _stripRect.anchorMax = new Vector2(0.5f, 1f);
            _stripRect.pivot = new Vector2(0.5f, 1f);
            _stripRect.anchoredPosition = new Vector2(0f, -14f);
            _stripRect.sizeDelta = new Vector2(StripWidth, StripHeight);
            bgGo.AddComponent<RectMask2D>();

            // srodkowy znacznik "wprost przede mna"
            var centerGo = new GameObject("CenterTick");
            centerGo.transform.SetParent(_stripRect, false);
            var centerImg = centerGo.AddComponent<Image>();
            centerImg.color = new Color(1f, 1f, 1f, 0.9f);
            var centerRect = centerGo.GetComponent<RectTransform>();
            centerRect.anchorMin = new Vector2(0.5f, 0f);
            centerRect.anchorMax = new Vector2(0.5f, 1f);
            centerRect.pivot = new Vector2(0.5f, 0.5f);
            centerRect.sizeDelta = new Vector2(2f, 0f);
            centerRect.anchoredPosition = Vector2.zero;

            foreach (var (label, bearing) in new (string, float)[]
            {
                ("N", 0f), ("NE", 45f), ("E", 90f), ("SE", 135f),
                ("S", 180f), ("SW", 225f), ("W", 270f), ("NW", 315f)
            })
            {
                var go = new GameObject($"Cardinal_{label}");
                go.transform.SetParent(_stripRect, false);
                var txt = go.AddComponent<TextMeshProUGUI>();
                var font = GetFont();
                if (font != null) txt.font = font;
                txt.text = label;
                txt.fontSize = label.Length == 1 ? 15f : 11f;
                txt.alignment = TextAlignmentOptions.Center;
                txt.color = label == "N" ? new Color(1f, 0.3f, 0.3f) : new Color(1f, 1f, 1f, 0.75f);
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(40f, StripHeight);
                _cardinalMarkers[label] = rt;
                rt.gameObject.AddComponent<CardinalTag>().Bearing = bearing;
            }
        }

        private class CardinalTag : MonoBehaviour
        {
            public float Bearing;
        }

        private void UpdateCompass()
        {
            if (_rootGo == null)
                return;

            _rootGo.SetActive(true);

            Vector3 playerPos = Player.m_localPlayer.transform.position;
            Camera cam = GameCamera.instance != null ? GameCamera.instance.GetComponent<Camera>() : null;
            Vector3 forward = cam != null ? cam.transform.forward : Player.m_localPlayer.transform.forward;
            float playerYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

            foreach (var kv in _cardinalMarkers)
            {
                float bearing = kv.Value.GetComponent<CardinalTag>().Bearing;
                PositionOnStrip(kv.Value, bearing, playerYaw);
            }

            RemoveDeletedPins();

            var circledNow = CircledPins.ToList();

            foreach (var stale in _compassMarkers.Keys.Where(p => !circledNow.Contains(p)).ToList())
            {
                if (_compassMarkers[stale].Root != null)
                    Destroy(_compassMarkers[stale].Root.gameObject);
                _compassMarkers.Remove(stale);
            }

            foreach (var pin in circledNow)
            {
                if (!_compassMarkers.TryGetValue(pin, out var marker))
                {
                    marker = CreateCompassMarker(pin);
                    if (marker == null) continue;
                    _compassMarkers[pin] = marker;
                }

                Vector3 toTarget = pin.m_pos - playerPos;
                float distance = toTarget.magnitude;
                float bearing = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;

                bool visible = PositionOnStrip(marker.Root, bearing, playerYaw);
                marker.Root.gameObject.SetActive(visible);
                marker.DistanceText.text = FormatDistance(distance);
            }
        }

        private PinMarker CreateCompassMarker(Minimap.PinData pin)
        {
            var go = new GameObject($"PinMarker_{pin.m_name}", typeof(RectTransform));
            go.transform.SetParent(_stripRect, false);

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(go.transform, false);
            var icon = iconGo.AddComponent<Image>();
            icon.sprite = pin.m_icon;
            icon.preserveAspect = true;
            var iconRect = iconGo.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(14f, 14f);
            iconRect.anchoredPosition = new Vector2(0f, 1f);

            var distGo = new GameObject("Dist");
            distGo.transform.SetParent(go.transform, false);
            var distText = distGo.AddComponent<TextMeshProUGUI>();
            var font = GetFont();
            if (font != null) distText.font = font;
            distText.fontSize = 9f;
            distText.alignment = TextAlignmentOptions.Center;
            distText.color = new Color(1f, 0.85f, 0.3f);
            var distRect = distGo.GetComponent<RectTransform>();
            distRect.anchorMin = new Vector2(0.5f, 0f);
            distRect.anchorMax = new Vector2(0.5f, 0f);
            distRect.pivot = new Vector2(0.5f, 0.5f);
            distRect.sizeDelta = new Vector2(50f, 12f);
            distRect.anchoredPosition = new Vector2(0f, -3f);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(50f, StripHeight);

            return new PinMarker { Root = rt, DistanceText = distText };
        }

        private static bool PositionOnStrip(RectTransform rect, float bearingDeg, float playerYawDeg)
        {
            float delta = Mathf.DeltaAngle(playerYawDeg, bearingDeg);
            bool visible = Mathf.Abs(delta) <= StripHalfFovDeg;
            float x = delta / StripHalfFovDeg * (StripWidth / 2f);
            rect.anchoredPosition = new Vector2(x, rect.anchoredPosition.y);
            return visible;
        }

        private static string FormatDistance(float meters)
        {
            return meters >= 1000f ? $"{meters / 1000f:0.0}km" : $"{Mathf.RoundToInt(meters)}m";
        }
    }
}
