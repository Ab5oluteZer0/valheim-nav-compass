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

namespace NavCompass
{
    // Roza wiatrow na HUD-zie pokazujaca kierunek do recznie oznaczonych pinow na mapie.
    //
    // Klikniecie pinu na mapie przelacza jego stan (wlasny system, niezalezny od wbudowanego
    // PinData.m_checked - gra ma juz swoj wlasny wizualny "checked" stan, wiec nie dublujemy go):
    //   None -> Circled (cienki jasnoniebieski pierscien) -> None
    // Tylko piny w stanie Circled sa pokazywane na kompasie na gorze ekranu.
    // Oznaczenia sa zapisywane do pliku per-swiat (BepInEx/plugins/NavCompass/tracked_<swiat>.json),
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
        public const string PluginVersion = "1.0.0";

        private const float StripWidth = 480f;
        // Wysokosc miesci ikone sledzonego pinu i pod nia napis (nazwa + dystans).
        private const float StripHeight = 36f;
        private const float MarkerIconSize = 18f;
        private const float MarkerLabelMaxWidth = 140f;
        private const float StripHalfFovDeg = 90f;
        private const float FramePadding = 6f;
        private static readonly Color ValheimOrange = new Color(1f, 0.631f, 0.235f, 1f);
        private static readonly Color ValheimBeige = new Color(0.8529f, 0.725f, 0.5331f, 1f);
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
            // Gra prosi mody o ustawienie tej flagi: w menu pojawia sie napis, ze gra jest
            // zmodowana (Iron Gate wymaga oznaczania modow jako nieoficjalnych).
            Game.isModded = true;
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

        // Do wersji 0.1.x DLL nazywal sie "Mod6-NavCompass" i lezal w takim folderze pluginow -
        // zapis zaznaczen jest obok DLL, wiec po zmianie nazwy przenosimy go jednorazowo.
        private const string LegacyPluginFolder = "Mod6-NavCompass";

        private static void MigrateLegacySaveFile(string path)
        {
            if (File.Exists(path))
                return;
            string pluginsDir = Path.GetDirectoryName(SaveDir);
            if (pluginsDir == null)
                return;
            string legacyPath = Path.Combine(pluginsDir, LegacyPluginFolder, Path.GetFileName(path));
            if (!File.Exists(legacyPath) || string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                File.Copy(legacyPath, path);
                Log.LogInfo($"Przeniesiono zaznaczone piny ze starego folderu: {legacyPath} -> {path}");
            }
            catch (Exception e)
            {
                Log.LogWarning($"Nie udalo sie przeniesc zaznaczen ze starego folderu ({legacyPath}): {e}");
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
            MigrateLegacySaveFile(path);
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

            // Drewniana ramka jak w oknach gry, a w niej pasek z przycinaniem (RectMask2D) -
            // znaczniki wyjezdzajace poza pasek nie wchodza na obramowanie.
            var frameGo = new GameObject("StripFrame", typeof(RectTransform));
            frameGo.transform.SetParent(_rootGo.transform, false);
            var frameRect = frameGo.GetComponent<RectTransform>();
            frameRect.anchorMin = new Vector2(0.5f, 1f);
            frameRect.anchorMax = new Vector2(0.5f, 1f);
            frameRect.pivot = new Vector2(0.5f, 1f);
            frameRect.anchoredPosition = new Vector2(0f, -10f);
            frameRect.sizeDelta = new Vector2(StripWidth + 2f * FramePadding, StripHeight + 2f * FramePadding);
            ApplyWoodFrameStyle(frameGo.AddComponent<Image>(), frameRect.sizeDelta.y);

            var bgGo = new GameObject("StripBg", typeof(RectTransform));
            bgGo.transform.SetParent(frameRect, false);
            _stripRect = bgGo.GetComponent<RectTransform>();
            _stripRect.anchorMin = new Vector2(0.5f, 0.5f);
            _stripRect.anchorMax = new Vector2(0.5f, 0.5f);
            _stripRect.pivot = new Vector2(0.5f, 0.5f);
            _stripRect.anchoredPosition = Vector2.zero;
            _stripRect.sizeDelta = new Vector2(StripWidth, StripHeight);
            bgGo.AddComponent<RectMask2D>();

            // srodkowy znacznik "wprost przede mna"
            var centerGo = new GameObject("CenterTick");
            centerGo.transform.SetParent(_stripRect, false);
            var centerImg = centerGo.AddComponent<Image>();
            centerImg.color = ValheimOrange;
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
                txt.color = label == "N" ? new Color(1f, 0.3f, 0.3f) : ValheimBeige;
                if (label.Length == 1)
                    StyleAsWoodLetter(txt, label == "N");
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(40f, StripHeight);
                _cardinalMarkers[label] = rt;
                rt.gameObject.AddComponent<CardinalTag>().Bearing = bearing;
            }
        }

        // Ta sama grafika i material co drewniane okna gry (i panele Jotunna) - brane wprost z
        // zaladowanych zasobow gry, bez zaleznosci od Jotunna. Obramowanie cietej grafiki jest
        // projektowane pod duze okna; na cienkim pasku zmniejszamy je (pixelsPerUnitMultiplier),
        // tak zeby gorna+dolna krawedz zajmowaly najwyzej polowe wysokosci ramki.
        private static void ApplyWoodFrameStyle(Image image, float frameHeight)
        {
            var sprite = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == "woodpanel_trophys");
            if (sprite == null)
            {
                Log.LogWarning("Nie znaleziono grafiki 'woodpanel_trophys' w zasobach gry - kompas zostaje na zwyklym, polprzezroczystym tle.");
                image.color = new Color(0f, 0f, 0f, 0.25f);
                return;
            }

            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            var material = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m.name == "litpanel");
            if (material != null)
                image.material = material;

            float verticalBorder = sprite.border.y + sprite.border.w;
            if (verticalBorder > 0f)
            {
                const float referencePixelsPerUnit = 100f;
                float needed = verticalBorder * referencePixelsPerUnit / (sprite.pixelsPerUnit * 0.5f * frameHeight);
                image.pixelsPerUnitMultiplier = Mathf.Max(1f, needed);
            }
        }

        // Glowne kierunki (N/E/S/W) "wystrugane z drewna": nordycka czcionka gry (Norsebold),
        // tekstura deski z gry jako powierzchnia liter, ciemny obrys jak wypalona krawedz i
        // cien pod spodem. Wszystko brane z zaladowanych zasobow gry - mod nie wozi ze soba
        // zadnej grafiki. Brak czegokolwiek = zostaje zwykly wyglad litery, bez bledu.
        private Material _woodLetterMaterial;
        private bool _woodLetterMaterialTried;

        private void StyleAsWoodLetter(TextMeshProUGUI txt, bool north)
        {
            var norse = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                .OrderByDescending(f => f.name.IndexOf("Norsebold", StringComparison.OrdinalIgnoreCase) >= 0)
                .FirstOrDefault(f => f.name.IndexOf("Norse", StringComparison.OrdinalIgnoreCase) >= 0);
            if (norse != null)
                txt.font = norse;

            var material = GetWoodLetterMaterial(txt.font);
            if (material == null)
                return;

            txt.fontSharedMaterial = material;
            txt.fontSize = 20f;
            txt.extraPadding = true;
            // Kolor wierzcholkow mnozy sie z tekstura - N zostaje czerwonawym drewnem, zeby
            // polnoc dalej odrozniala sie od reszty na pierwszy rzut oka.
            txt.color = north ? new Color(1f, 0.55f, 0.45f) : Color.white;
        }

        private Material GetWoodLetterMaterial(TMP_FontAsset font)
        {
            if (_woodLetterMaterialTried)
                return _woodLetterMaterial;
            _woodLetterMaterialTried = true;

            // Pelny shader SDF (nie "Mobile") - tylko on ma teksture powierzchni (_FaceTex).
            var shader = Shader.Find("TextMeshPro/Distance Field");
            if (font == null || font.material == null || shader == null)
            {
                Log.LogWarning("Kompas: brak czcionki albo shadera 'TextMeshPro/Distance Field' - litery N/E/S/W bez drewna.");
                return null;
            }

            var material = new Material(font.material) { name = "NavCompassWoodLetters", shader = shader };
            string woodSource = ApplyWoodFaceTexture(material);
            material.SetColor("_FaceColor", Color.white);
            material.SetFloat("_FaceDilate", 0.1f);
            material.SetFloat("_OutlineWidth", 0.25f);
            material.SetColor("_OutlineColor", new Color(0.16f, 0.09f, 0.04f, 1f));
            material.EnableKeyword("UNDERLAY_ON");
            material.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.6f));
            material.SetFloat("_UnderlayOffsetX", 0.4f);
            material.SetFloat("_UnderlayOffsetY", -0.4f);
            material.SetFloat("_UnderlaySoftness", 0.3f);
            // Bez przeliczenia proporcji obrys/cien bylyby liczone dla starych wartosci i ucinane.
            ShaderUtilities.UpdateShaderRatios(material);

            Log.LogInfo($"Kompas: drewniane litery N/E/S/W - czcionka '{font.name}', drewno: {woodSource}.");
            _woodLetterMaterial = material;
            return material;
        }

        // Deska z gry (Planks1c: cieple, poziome deski). Gdy nie jest akurat w pamieci - drewno
        // z grafiki okien gry (woodpanel_trophys, zawsze wczytana), bez jej obramowania.
        private static string ApplyWoodFaceTexture(Material material)
        {
            var planks = Resources.FindObjectsOfTypeAll<Texture2D>().FirstOrDefault(t => t.name == "Planks1c");
            if (planks != null)
            {
                material.SetTexture("_FaceTex", planks);
                // Polowa wysokosci tekstury na litere - 2-3 deski zamiast drobnych paskow.
                material.SetTextureScale("_FaceTex", new Vector2(1f, 0.5f));
                material.SetTextureOffset("_FaceTex", Vector2.zero);
                return "Planks1c";
            }

            var panel = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == "woodpanel_trophys");
            if (panel != null && panel.texture != null)
            {
                var r = panel.textureRect;
                var b = panel.border; // x=lewo, y=dol, z=prawo, w=gora
                float w = panel.texture.width, h = panel.texture.height;
                // Fragment srodka panelu (bez ramki) - caly srodek na jedna litere dalby zbyt drobne slojowanie.
                float innerW = (r.width - b.x - b.z) * 0.3f, innerH = (r.height - b.y - b.w) * 0.3f;
                material.SetTexture("_FaceTex", panel.texture);
                material.SetTextureScale("_FaceTex", new Vector2(innerW / w, innerH / h));
                material.SetTextureOffset("_FaceTex", new Vector2((r.x + b.x) / w, (r.y + b.y) / h));
                return "woodpanel_trophys (Planks1c niedostepne)";
            }

            return "BRAK (ani Planks1c, ani woodpanel_trophys) - litery w kolorze drewna bez slojow";
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

                string name = VisiblePinName(pin);
                marker.DistanceText.text = name == null ? FormatDistance(distance) : $"{name} {FormatDistance(distance)}";
            }
        }

        // Nazwa pinu na kompasie tylko wtedy, gdy pin ja ma (gracz ja nadal) i jego podpis jest
        // widoczny na mapie. O podpisach pinow Auto Waypoints decyduje tamten mod - pytany przez
        // jego publiczna metode IsPinLabelVisible, znaleziona przez Chainloader i refleksje, zeby
        // kompas dzialal tez bez niego (wtedy nazwa jest pokazywana zawsze, gdy pin ja ma).
        private const string AutoWaypointsGuid = "com.michal.valheim.autowaypoints";
        private Func<Minimap.PinData, bool> _isPinLabelVisible;
        private bool _labelVisibilityResolved;

        private string VisiblePinName(Minimap.PinData pin)
        {
            if (string.IsNullOrEmpty(pin.m_name))
                return null;
            if (!_labelVisibilityResolved)
            {
                _labelVisibilityResolved = true;
                _isPinLabelVisible = ResolveLabelVisibility();
            }
            if (_isPinLabelVisible != null && !_isPinLabelVisible(pin))
                return null;
            // Piny dodane przez gre maja klucze tlumaczen ("$enemy_bonemass") - jak na mapie.
            return Localization.instance != null ? Localization.instance.Localize(pin.m_name) : pin.m_name;
        }

        private static Func<Minimap.PinData, bool> ResolveLabelVisibility()
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(AutoWaypointsGuid, out var info) || info.Instance == null)
                return null;
            var method = info.Instance.GetType().GetMethod("IsPinLabelVisible", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(Minimap.PinData) }, null);
            if (method == null || method.ReturnType != typeof(bool))
            {
                Log.LogWarning("Auto Waypoints nie udostepnia IsPinLabelVisible (starsza wersja?) - nazwy pinow na kompasie pokazywane zawsze.");
                return null;
            }
            Log.LogInfo("Nazwy pinow na kompasie wedlug ustawien podpisow z Auto Waypoints.");
            return (Func<Minimap.PinData, bool>)Delegate.CreateDelegate(typeof(Func<Minimap.PinData, bool>), info.Instance, method);
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
            // Ikona w gornej czesci paska, napis (nazwa + dystans) pod nia - OBA w calosci
            // wewnatrz paska: pasek przycina zawartosc (RectMask2D), a napis ustawiony ponizej
            // jego dolnej krawedzi byl praktycznie niewidoczny (zostawaly z niego 3 piksele).
            var iconRect = iconGo.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.5f, 1f);
            iconRect.anchorMax = new Vector2(0.5f, 1f);
            iconRect.pivot = new Vector2(0.5f, 1f);
            iconRect.sizeDelta = new Vector2(MarkerIconSize, MarkerIconSize);
            iconRect.anchoredPosition = new Vector2(0f, -2f);

            float labelHeight = StripHeight - MarkerIconSize - 2f;

            var distGo = new GameObject("Dist");
            distGo.transform.SetParent(go.transform, false);
            var distText = distGo.AddComponent<TextMeshProUGUI>();
            var font = GetFont();
            if (font != null) distText.font = font;
            distText.fontSize = 13f;
            distText.enableAutoSizing = true;
            distText.fontSizeMin = 10f;
            distText.fontSizeMax = 13f;
            distText.fontStyle = FontStyles.Bold;
            distText.alignment = TextAlignmentOptions.Center;
            distText.color = new Color(1f, 0.93f, 0.75f);
            // Ciemny obrys liter zamiast tla - jasny tekst odcina sie od drewna paska.
            distText.outlineWidth = 0.2f;
            distText.outlineColor = new Color32(20, 12, 5, 255);
            distText.overflowMode = TextOverflowModes.Ellipsis;
            distText.raycastTarget = false;
            var distRect = distGo.GetComponent<RectTransform>();
            distRect.anchorMin = new Vector2(0.5f, 0f);
            distRect.anchorMax = new Vector2(0.5f, 0f);
            distRect.pivot = new Vector2(0.5f, 0f);
            distRect.sizeDelta = new Vector2(MarkerLabelMaxWidth, labelHeight);
            distRect.anchoredPosition = new Vector2(0f, 1f);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(MarkerLabelMaxWidth, StripHeight);

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
