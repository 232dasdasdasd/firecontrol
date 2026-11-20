#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8625
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Notruf112ModMenu
{
    public partial class ModMenu
    {
        private static ModMenu fcLoadingOwner;
        private Harmony fcLoadingHarmony;
        private bool fcLoadingPatched;
        private AssetBundle fcLoadingLastBundle;
        private readonly List<Sprite> fcLoadingSprites = new List<Sprite>();
        private readonly List<string> fcLoadingTexts = new List<string>();
        private int fcLoadingSelection = -1;
        private readonly List<FCLoadingVisual> fcLoadingVisuals = new List<FCLoadingVisual>();

        private static readonly string[] FCLoadingDescriptions =
        {
            "Ein Pkw stand beim Eintreffen der Feuerwehr bereits in Vollbrand. Einsatzkräfte bekämpften die Flammen und verhinderten eine Ausbreitung auf weitere Fahrzeuge und die Umgebung.",
            "Auf der Autobahn A57 geriet ein Pkw in Brand. Die Fahrerin konnte sich rechtzeitig in Sicherheit bringen. Ein Atemschutztrupp löschte das brennende Fahrzeug.",
            "Ein Kellerbrand verursachte starke Rauchentwicklung in einem Reihenhaus. Die Feuerwehr bekämpfte das Feuer und verhinderte eine weitere Ausbreitung. Das Gebäude war vorübergehend unbewohnbar.",
            "In einem Industriebetrieb trat Ammoniak aus einer Kühlanlage aus. Die Feuerwehr evakuierte den betroffenen Bereich und kontrollierte die Umgebung mit spezieller Messtechnik.",
            "Eine Explosion zerstörte einen abgestellten Imbisswagen. Die Feuerwehr sicherte die Einsatzstelle, kontrollierte mögliche Gefahren durch Gasflaschen und unterstützte die weiteren Maßnahmen.",
            "Ein beschädigter Kanister mit Gefahrenkennzeichnung sorgte für einen Feuerwehreinsatz. Die ausgelaufene Flüssigkeit wurde untersucht, aufgenommen und anschließend fachgerecht entsorgt."
        };

        private static readonly string[] FCLoadingTitles =
        {
            "PKW-VOLLBRAND  /  KLEVE",
            "FAHRZEUGBRAND A57  /  GOCH",
            "KELLERBRAND  /  GOCH",
            "AMMONIAKAUSTRITT  /  KLEVE",
            "IMBISSWAGEN-EXPLOSION  /  GOCH",
            "GEFAHRSTOFFEINSATZ  /  GOCH"
        };

        private void FCLoadingInitialize()
        {
            fcLoadingOwner = this;
            FCLoadingTryPatch();
        }

        private void FCLoadingUpdate()
        {
            if (!fcLoadingPatched) FCLoadingTryPatch();
            if (fcVehicleBundle != fcLoadingLastBundle)
            {
                fcLoadingLastBundle = fcVehicleBundle;
                FCLoadingLoadImages();
                FCLoadingRefreshVisuals();
            }
            for (int i = fcLoadingVisuals.Count - 1; i >= 0; --i)
                if (fcLoadingVisuals[i] == null) fcLoadingVisuals.RemoveAt(i);
        }

        private void FCLoadingShutdown()
        {
            try { fcLoadingHarmony?.UnpatchSelf(); }
            catch (Exception ex) { Debug.LogWarning("[FIRE CONTROL LoadingScreen] Unpatch: " + ex.Message); }
            fcLoadingHarmony = null;
            fcLoadingPatched = false;
            for (int i = 0; i < fcLoadingVisuals.Count; ++i)
                if (fcLoadingVisuals[i] != null) Destroy(fcLoadingVisuals[i]);
            fcLoadingVisuals.Clear();
            if (fcLoadingOwner == this) fcLoadingOwner = null;
            fcLoadingSprites.Clear();
            fcLoadingTexts.Clear();
            fcLoadingLastBundle = null;
        }

        private void FCLoadingTryPatch()
        {
            try
            {
                Type type = AccessTools.TypeByName("LoadingTip");
                if (type == null) return;
                MethodInfo refresh = AccessTools.Method(type, "RefreshTip");
                if (refresh == null) return;
                fcLoadingHarmony = new Harmony("de.notruf112.firecontrol.loadingscreen");
                fcLoadingHarmony.Patch(refresh, prefix: new HarmonyMethod(typeof(ModMenu).GetMethod(nameof(FCLoadingRefreshPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                MethodInfo start = AccessTools.Method(type, "Start");
                if (start != null)
                    fcLoadingHarmony.Patch(start, postfix: new HarmonyMethod(typeof(ModMenu).GetMethod(nameof(FCLoadingStartPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                fcLoadingPatched = true;
                Debug.Log("[FIRE CONTROL LoadingScreen] LoadingTip gepatcht; Design V2 aktiv.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[FIRE CONTROL LoadingScreen] Patch fehlgeschlagen: " + ex);
                try { fcLoadingHarmony?.UnpatchSelf(); } catch { }
                fcLoadingHarmony = null;
            }
        }

        private static bool FCLoadingRefreshPrefix(object __instance)
        {
            if (fcLoadingOwner != null) fcLoadingOwner.FCLoadingApply(__instance);
            else FCLoadingClear(__instance);
            return false;
        }

        private static void FCLoadingStartPostfix(object __instance)
        {
            if (fcLoadingOwner != null) fcLoadingOwner.FCLoadingApply(__instance);
            else FCLoadingClear(__instance);
        }

        private void FCLoadingLoadImages()
        {
            fcLoadingSprites.Clear();
            fcLoadingTexts.Clear();
            fcLoadingSelection = -1;
            if (fcVehicleBundle == null) return;
            string[] assetNames;
            try { assetNames = fcVehicleBundle.GetAllAssetNames(); }
            catch (Exception ex) { Debug.LogWarning("[FIRE CONTROL LoadingScreen] Bundle-Liste: " + ex.Message); return; }
            for (int i = 1; i <= FCLoadingDescriptions.Length; i++)
            {
                string basename = "loading_" + i.ToString("00");
                string match = null;
                foreach (string assetName in assetNames)
                {
                    string filename = System.IO.Path.GetFileNameWithoutExtension(assetName);
                    if (string.Equals(filename, basename, StringComparison.OrdinalIgnoreCase)) { match = assetName; break; }
                }
                if (match == null) { Debug.LogWarning("[FIRE CONTROL LoadingScreen] Nicht im Bundle: " + basename); continue; }
                Sprite sprite = null;
                try
                {
                    sprite = fcVehicleBundle.LoadAsset<Sprite>(match);
                    if (sprite == null)
                    {
                        Texture2D texture = fcVehicleBundle.LoadAsset<Texture2D>(match);
                        if (texture != null)
                            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[FIRE CONTROL LoadingScreen] Bild " + basename + ": " + ex.Message); }
                if (sprite == null) continue;
                fcLoadingSprites.Add(sprite);
                fcLoadingTexts.Add(FCLoadingDescriptions[i - 1]);
                Debug.Log("[FIRE CONTROL LoadingScreen] Geladen: " + match);
            }
            Debug.Log("[FIRE CONTROL LoadingScreen] Eigene Ladebilder: " + fcLoadingSprites.Count);
        }

        private void FCLoadingRefreshVisuals()
        {
            for (int i = fcLoadingVisuals.Count - 1; i >= 0; --i)
            {
                FCLoadingVisual visual = fcLoadingVisuals[i];
                if (visual == null) { fcLoadingVisuals.RemoveAt(i); continue; }
                if (fcLoadingSprites.Count > 0)
                {
                    int pick = UnityEngine.Random.Range(0, fcLoadingSprites.Count);
                    visual.SetContent(fcLoadingSprites[pick], fcLoadingTexts[pick], FCLoadingGetTitle(fcLoadingTexts[pick]), pick + 1, fcLoadingSprites.Count);
                }
                else visual.SetContent(null, "", "", 0, 0);
            }
        }

        private static string FCLoadingGetTitle(string description)
        {
            for (int i = 0; i < FCLoadingDescriptions.Length; ++i)
                if (string.Equals(description, FCLoadingDescriptions[i], StringComparison.Ordinal)) return FCLoadingTitles[i];
            return "EINSATZBERICHT";
        }

        private void FCLoadingApply(object tip)
        {
            try
            {
                if (fcLoadingLastBundle != fcVehicleBundle)
                {
                    fcLoadingLastBundle = fcVehicleBundle;
                    FCLoadingLoadImages();
                }
                Sprite sprite = null;
                string description = "";
                string title = "";
                int number = 0;
                if (fcLoadingSprites.Count > 0)
                {
                    fcLoadingSelection = UnityEngine.Random.Range(0, fcLoadingSprites.Count);
                    sprite = fcLoadingSprites[fcLoadingSelection];
                    description = fcLoadingTexts[fcLoadingSelection];
                    title = FCLoadingGetTitle(description);
                    number = fcLoadingSelection + 1;
                }

                // Original-UI stets leeren; eigene Oberfläche liegt darüber.
                FCLoadingClear(tip);
                Component component = tip as Component;
                if (component == null) return;
                FCLoadingVisual visual = component.GetComponent<FCLoadingVisual>();
                if (visual == null) visual = component.gameObject.AddComponent<FCLoadingVisual>();
                if (!fcLoadingVisuals.Contains(visual)) fcLoadingVisuals.Add(visual);
                visual.SetContent(sprite, description, title, number, fcLoadingSprites.Count);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FIRE CONTROL LoadingScreen] Anzeige: " + ex);
                FCLoadingClear(tip);
            }
        }

        private static void FCLoadingClear(object tip)
        {
            try
            {
                object image = FCLoadingField(tip, "tipImage");
                object label = FCLoadingField(tip, "tipLabel");
                FCLoadingSetProperty(label, "text", "");
                FCLoadingSetProperty(image, "sprite", null);
                Component imageComponent = image as Component;
                if (imageComponent != null) imageComponent.gameObject.SetActive(false);
            }
            catch (Exception ex) { Debug.LogWarning("[FIRE CONTROL LoadingScreen] Leeren: " + ex.Message); }
        }

        private static object FCLoadingField(object target, string name)
        {
            if (target == null) return null;
            FieldInfo field = AccessTools.Field(target.GetType(), name);
            return field != null ? field.GetValue(target) : null;
        }

        private static void FCLoadingSetProperty(object target, string name, object value)
        {
            if (target == null) return;
            PropertyInfo property = AccessTools.Property(target.GetType(), name);
            if (property != null && property.CanWrite) property.SetValue(target, value, null);
        }
    }

    // Eigenstaendige, bildschirmfuellende UI. Bewusst NICHT unter dem LoadingTip
    // geparentet: ein RectTransform innerhalb des alten Layouts wuerde geclippt/skaliert.
    internal sealed class FCLoadingVisual : MonoBehaviour
    {
        private static readonly Color Bg = new Color(0.018f, 0.026f, 0.041f, 1f);
        private static readonly Color Panel = new Color(0.055f, 0.073f, 0.096f, 1f);
        private static readonly Color Red = new Color(0.95f, 0.16f, 0.11f, 1f);
        private static readonly Color White = new Color(0.96f, 0.97f, 0.99f, 1f);
        private static readonly Color Muted = new Color(0.66f, 0.72f, 0.79f, 1f);
        private GameObject canvasObject;
        private Canvas canvas;
        private Image photo;
        private Text titleText, descriptionText, counterText, statusText;
        private RectTransform barTrack, barSegment;
        private RectTransform[] flameParts;
        private Image[] embers;
        private RectTransform[] emberRects;
        private Font font;
        private bool built;
        private float clock, fade;
        private Sprite shownSprite;
        private string shownDescription;

        private void Awake()
        {
            try { Build(); }
            catch (Exception ex) { Debug.LogError("[FIRE CONTROL LoadingScreen] Build: " + ex); }
        }

        private void OnEnable() { if (canvas != null && canvasObject != null) canvas.enabled = true; }
        private void OnDisable() { if (canvas != null && canvasObject != null) canvas.enabled = false; }
        private void OnDestroy()
        {
            if (canvasObject != null) Destroy(canvasObject);
        }

        internal void SetContent(Sprite sprite, string description, string title, int number, int total)
        {
            // Bei Szenenwechsel kann der separate Root-Canvas bereits zerstoert sein.
            // Ein neues LoadingTip.RefreshTip darf ihn fuer den naechsten Ladevorgang neu aufbauen.
            if (built && (canvasObject == null || canvas == null))
            {
                built = false;
                canvasObject = null;
                canvas = null;
            }
            if (!built) Build();
            if (!built || canvas == null || canvasObject == null) return;
            if (shownSprite != sprite || shownDescription != description) fade = 0f;
            shownSprite = sprite;
            shownDescription = description;
            photo.sprite = sprite;
            photo.enabled = sprite != null;
            titleText.text = title ?? "";
            descriptionText.text = description ?? "";
            counterText.text = total > 0
                ? "EINSATZDOKUMENTATION  /  " + number.ToString("00") + " VON " + total.ToString("00")
                : "EINSATZDOKUMENTATION";
            canvas.enabled = isActiveAndEnabled && gameObject.activeInHierarchy;
        }

        private void LateUpdate()
        {
            if (!built) return;
            // Root-Canvas wird beim Szenenwechsel von Unity zerstoert.
            // Unity-Objekte koennen dann als null gelten, obwohl der C#-Wrapper noch existiert.
            if (canvasObject == null || canvas == null)
            {
                built = false;
                canvasObject = null;
                canvas = null;
                enabled = false;
                return;
            }
            // Wird die originale LoadingTip-Komponente deaktiviert, verschwindet auch unser Overlay.
            canvas.enabled = isActiveAndEnabled && gameObject.activeInHierarchy;
            if (!canvas.enabled) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            clock += dt;
            fade = Mathf.Min(1f, fade + dt * 2.1f);
            if (photo != null) photo.color = new Color(1f, 1f, 1f, fade);
            if (statusText != null)
                statusText.text = "SPIEL WIRD GELADEN" + new string('.', 1 + ((int)(clock * 2f) % 3));
            if (barTrack != null && barSegment != null)
            {
                float width = barTrack.rect.width;
                float segment = Mathf.Max(36f, width * 0.22f);
                float travel = Mathf.Max(0f, width - segment);
                barSegment.anchorMin = new Vector2(0f, 0f);
                barSegment.anchorMax = new Vector2(0f, 1f);
                barSegment.pivot = new Vector2(0f, 0.5f);
                barSegment.anchoredPosition = new Vector2(Mathf.PingPong(clock * 0.43f, 1f) * travel, 0f);
                barSegment.sizeDelta = new Vector2(segment, 0f);
            }
            if (flameParts != null)
                for (int i = 0; i < flameParts.Length; i++)
                {
                    float pulse = Mathf.Sin(clock * (2.4f + i * 0.2f) + i);
                    flameParts[i].localScale = new Vector3(1f + pulse * 0.05f, 1f + pulse * 0.14f, 1f);
                }
            if (embers != null)
                for (int i = 0; i < embers.Length; i++)
                {
                    float t = Mathf.Repeat(clock * (0.23f + i * 0.018f) + i * 0.19f, 1f);
                    emberRects[i].anchorMin = emberRects[i].anchorMax = new Vector2(0.2f + (i * 0.13f) % 0.65f, t);
                    emberRects[i].anchoredPosition = Vector2.zero;
                    embers[i].color = new Color(1f, 0.3f + i % 3 * 0.13f, 0.08f, (1f - t) * 0.6f);
                }
        }

        private void Build()
        {
            if (built) return;
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            // Ein eigener ROOT-Canvas verhindert, dass alte LayoutGroups, Masken,
            // CanvasScaler oder RectTransforms unser neues Layout verschieben.
            canvasObject = new GameObject("FC_LoadingScreen_Fullscreen", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32760;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            RectTransform root = canvasObject.GetComponent<RectTransform>();

            // Die erste Image ist zu 100 % deckend und ueberlagert ALLE alten Bilder.
            Image background = Image("OpaqueBackground", root, Bg);
            Stretch(background.rectTransform, 0f, 0f, 1f, 1f);
            Image topBand = Image("TopBand", root, new Color(0.036f, 0.047f, 0.064f, 1f));
            Stretch(topBand.rectTransform, 0f, 0.79f, 1f, 1f);
            Image leftRed = Image("RedAccent", root, Red);
            Stretch(leftRed.rectTransform, 0f, 0f, 0.005f, 1f);
            Image headerRule = Image("HeaderRule", root, new Color(0.42f, 0.12f, 0.12f, 1f));
            Stretch(headerRule.rectTransform, 0.04f, 0.79f, 0.96f, 0.793f);

            // Header: feste normierte Bereiche statt gemischter Pixel-Offsets.
            Text brand = Label("Brand", root, "FIRE CONTROL", 70, White, TextAnchor.MiddleLeft, FontStyle.Bold);
            Stretch(brand.rectTransform, 0.055f, 0.865f, 0.74f, 0.963f);
            Text tagline = Label("Tagline", root, "NOTRUF 112  |  DIE FEUERWEHR SIMULATION", 22, Muted, TextAnchor.MiddleLeft);
            Stretch(tagline.rectTransform, 0.058f, 0.811f, 0.74f, 0.862f);
            Image brandRed = Image("BrandStripe", root, Red);
            Stretch(brandRed.rectTransform, 0.055f, 0.859f, 0.16f, 0.864f);

            // Linkes Bild und rechte Textkarte: niemals ueberlappende Anker.
            Image photoPanel = Image("PhotoPanel", root, Panel);
            Stretch(photoPanel.rectTransform, 0.055f, 0.205f, 0.652f, 0.748f);
            Image photoBacking = Image("PhotoBacking", photoPanel.rectTransform, Color.black);
            Stretch(photoBacking.rectTransform, 0.008f, 0.012f, 0.992f, 0.988f);
            photo = Image("IncidentPhoto", photoBacking.rectTransform, Color.white);
            Stretch(photo.rectTransform, 0f, 0f, 1f, 1f);
            photo.preserveAspect = true;
            photo.enabled = false;
            Image photoAccent = Image("PhotoAccent", photoPanel.rectTransform, Red);
            Stretch(photoAccent.rectTransform, 0f, 0f, 1f, 0.012f);

            Image info = Image("InfoPanel", root, Panel);
            Stretch(info.rectTransform, 0.67f, 0.205f, 0.945f, 0.748f);
            Image infoAccent = Image("InfoAccent", info.rectTransform, Red);
            Stretch(infoAccent.rectTransform, 0f, 0f, 0.009f, 1f);
            counterText = Label("Counter", info.rectTransform, "EINSATZDOKUMENTATION", 17, Red, TextAnchor.MiddleLeft, FontStyle.Bold);
            Stretch(counterText.rectTransform, 0.075f, 0.865f, 0.95f, 0.96f);
            titleText = Label("Title", info.rectTransform, "", 29, White, TextAnchor.UpperLeft, FontStyle.Bold);
            Stretch(titleText.rectTransform, 0.075f, 0.66f, 0.95f, 0.845f);
            Image separator = Image("Separator", info.rectTransform, new Color(0.23f, 0.27f, 0.31f, 1f));
            Stretch(separator.rectTransform, 0.075f, 0.63f, 0.925f, 0.634f);
            descriptionText = Label("Description", info.rectTransform, "", 22, White, TextAnchor.UpperLeft);
            Stretch(descriptionText.rectTransform, 0.075f, 0.16f, 0.93f, 0.60f);
            descriptionText.horizontalOverflow = HorizontalWrapMode.Wrap;
            descriptionText.verticalOverflow = VerticalWrapMode.Truncate;
            Text location = Label("Location", info.rectTransform, "KREIS KLEVE  |  EINSATZARCHIV", 15, Muted, TextAnchor.MiddleLeft);
            Stretch(location.rectTransform, 0.075f, 0.025f, 0.95f, 0.12f);

            statusText = Label("LoadingStatus", root, "SPIEL WIRD GELADEN", 23, White, TextAnchor.MiddleLeft, FontStyle.Bold);
            Stretch(statusText.rectTransform, 0.058f, 0.117f, 0.67f, 0.177f);
            Text wait = Label("Wait", root, "BITTE WARTEN", 18, Muted, TextAnchor.MiddleRight);
            Stretch(wait.rectTransform, 0.69f, 0.117f, 0.942f, 0.177f);
            Image track = Image("ProgressTrack", root, new Color(0.18f, 0.21f, 0.25f, 1f));
            Stretch(track.rectTransform, 0.058f, 0.095f, 0.942f, 0.108f);
            barTrack = track.rectTransform;
            Image moving = Image("ProgressActivity", track.rectTransform, Red);
            barSegment = moving.rectTransform;
            Stretch(barSegment, 0f, 0f, 0.22f, 1f);
            Text footer = Label("Footer", root, "FIRE CONTROL  |  EMERGENCY OPERATIONS  |  3.5.0", 15, Muted, TextAnchor.MiddleLeft);
            Stretch(footer.rectTransform, 0.058f, 0.026f, 0.78f, 0.077f);

            // Zurueckhaltendes, animiertes 112-Flammenemblem rechts im Header.
            RectTransform flameArea = Rect("FlameArea", root);
            Stretch(flameArea, 0.79f, 0.81f, 0.945f, 0.976f);
            flameParts = new RectTransform[5];
            for (int i = 0; i < flameParts.Length; i++)
            {
                Image flame = Image("Flame_" + i, flameArea, new Color(1f, 0.21f + i * 0.09f, 0.07f, 0.32f));
                Stretch(flame.rectTransform, 0.18f + i * 0.13f, 0.06f, 0.27f + i * 0.13f, 0.72f + (i % 2) * 0.14f);
                flameParts[i] = flame.rectTransform;
            }
            Text emblem = Label("Emblem112", flameArea, "112", 74, White, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(emblem.rectTransform, 0.02f, 0.08f, 0.98f, 0.91f);
            embers = new Image[7];
            emberRects = new RectTransform[7];
            for (int i = 0; i < embers.Length; i++)
            {
                embers[i] = Image("Ember_" + i, flameArea, Red);
                emberRects[i] = embers[i].rectTransform;
                Stretch(emberRects[i], 0.2f, 0.2f, 0.215f, 0.22f);
            }
            built = true;
            Debug.Log("[FIRE CONTROL LoadingScreen] Fullscreen-Layout 2.1 erstellt.");
        }

        private static RectTransform Rect(string name, RectTransform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private static Image Image(string name, RectTransform parent, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private Text Label(string name, RectTransform parent, string value, int size, Color color,
            TextAnchor alignment, FontStyle style = FontStyle.Normal)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            Text label = go.GetComponent<Text>();
            label.font = font;
            label.fontSize = size;
            label.fontStyle = style;
            label.text = value;
            label.color = color;
            label.alignment = alignment;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = Mathf.Max(11, size / 2);
            label.resizeTextMaxSize = size;
            label.raycastTarget = false;
            return label;
        }

        private static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = new Vector2(left, bottom);
            rt.anchorMax = new Vector2(right, top);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
        }
    }
}
