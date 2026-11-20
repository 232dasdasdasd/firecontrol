#pragma warning disable CS8600, CS8603, CS8604, CS8618, CS8625

using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using BepInEx;
using BepInEx.Unity.Mono;
using UnityEngine;
using com.crenetic.Notruf2019.Game;
using com.crenetic.Notruf2019.Situation;
using com.crenetic.N2019Plugins.GameUnits;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Notruf112ModMenu
{
    public partial class ModMenu : BaseUnityPlugin
    {
        // Hallentor-Fix: ausschließlich für den lokalen Avatar während einer Door/Gateway-Interaktion.
        // Fix ist im laufenden Spiel dauerhaft aktiv. Kein Eingriff in Toranimation oder andere KI.

        private readonly List<VehicleEntry> vehicles = new List<VehicleEntry>();
        private Vector2 supportScroll;
        private Vector2 deployedScroll;
        private Vector2 dashboardScroll;
        private bool showSupportDiagnostics;
        private readonly HashSet<string> supportSelected = new HashSet<string>();
        private Vector2 vehicleScroll;
        private int expandedVehicle = -1;

        private GUIStyle titleStyle = null!;
        private GUIStyle headingStyle = null!;
        private GUIStyle textStyle = null!;
        private GUIStyle buttonStyle = null!;
        private GUIStyle activeButtonStyle = null!;
        private GUIStyle panelStyle = null!;
        private GUIStyle closeStyle = null!;
        private GUIStyle listStyle = null!;
        private GUIStyle windowStyle = null!;
        private Texture2D cursorTexture = null!;
        private Coroutine? cursorRoutine;
        private Behaviour? pausedPlayerBehaviour;
        private bool playerWasEnabled;

        private Texture2D darkTexture = null!;
        private Texture2D panelTexture = null!;
        private Texture2D redTexture = null!;

        // Verkehr 2.0: native Spieleinstellungen; kein künstliches Spawning.
        private Vector2 trafficScroll;
        private Vector2 timeDateScroll;
        private Vector2 navigationScroll;
        private float trafficCars = 0.25f;
        private float trafficPedestrians = 0.30f;
        private bool trafficValuesLoaded;
        private bool trafficYieldEnabled = true;
        private Harmony? trafficHarmony;
        private float nextTrafficEmergencyScan;
        private readonly List<Vector3> trafficEmergencyPositions = new List<Vector3>();
        private static ModMenu? trafficInstance;
        private static readonly BindingFlags TrafficFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static object? TrafficMember(object? target, string name)
        {
            if (target == null) return null;
            Type t = target.GetType();
            try { return t.GetProperty(name, TrafficFlags)?.GetValue(target, null) ?? t.GetField(name, TrafficFlags)?.GetValue(target); }
            catch { return null; }
        }

        private void LoadTrafficOptions()
        {
            try
            {
                var car = com.crenetic.Notruf2019.UI.GameConfig.GetOptionSafe<com.crenetic.Notruf2019.UI.GameOptionFloat>(com.crenetic.Notruf2019.UI.GameConfig.Option.TrafficDensityScale);
                var ped = com.crenetic.Notruf2019.UI.GameConfig.GetOptionSafe<com.crenetic.Notruf2019.UI.GameOptionFloat>(com.crenetic.Notruf2019.UI.GameConfig.Option.PedestrianDensity);
                if (car == null || ped == null) return;
                trafficCars = car.CurrentRelativeValue;
                trafficPedestrians = ped.CurrentRelativeValue;
                trafficValuesLoaded = true;
            }
            catch (Exception) { ; }
        }

        private void SetTrafficOptions(bool cars, float value)
        {
            value = Mathf.Clamp01(value);
            try
            {
                var option = cars ? com.crenetic.Notruf2019.UI.GameConfig.Option.TrafficDensityScale : com.crenetic.Notruf2019.UI.GameConfig.Option.PedestrianDensity;
                var gameOption = com.crenetic.Notruf2019.UI.GameConfig.GetOptionSafe<com.crenetic.Notruf2019.UI.GameOptionFloat>(option);
                if (gameOption == null) { statusMessage = "Verkehrssystem noch nicht bereit"; return; }
                gameOption.CurrentRelativeValue = value;
                gameOption.Save(option);
                if (cars) trafficCars = value; else trafficPedestrians = value;
                statusMessage = (cars ? "PKW" : "Fussgaenger") + "-Dichte: " + Mathf.RoundToInt(value * 100f) + "%";
            }
            catch (Exception) { statusMessage = "Verkehrsoption fehlgeschlagen"; ; }
        }

        // Persönliche Geh-/Laufgeschwindigkeit: skaliert nur die lokale Avatar-Steuerung.
        // Die Bewegungsengine kann Eingaben auf [-1,1] begrenzen; keine Physik-Teleports.
        private float playerMovementFactor = 1f;
        private Harmony? playerControlHarmony;
        private static ModMenu? playerControlInstance;
        private static bool tabWasPressed;
        private static bool tabReleaseFrame;
        private void DrawSettingsTab()
        {
            DrawTrafficTab();
            // DrawTrafficTab verwaltet bereits eine ScrollView; deshalb Regler darin integrieren.
        }
        private void InstallPlayerControlPatches()
        {
            playerControlInstance = this;
            try
            {
                playerControlHarmony = new Harmony("de.notruf112.firecontrol.player-control");
                // Keine Speed-Änderung mehr auf beliebigen NPC-Avataren.
                // Stattdessen ausschließlich im nachgewiesenen PlayerController.UpdateAvatar.
                // Spieler-Controller: sicherer Aufrufpunkt der tatsächlichen Spielfigur.
                Type? playerType = AccessTools.TypeByName("com.crenetic.Notruf2019.Game.PlayerController");
                MethodInfo? playerUpdate = playerType == null ? null : AccessTools.Method(playerType, "UpdateAvatar");
                if (playerUpdate != null)
                {
                    playerControlHarmony.Patch(playerUpdate, postfix: new HarmonyMethod(typeof(ModMenu), nameof(PlayerAvatarSpeedPostfix)));
                    ;
                }
                Type? inputType = AccessTools.TypeByName("com.crenetic.Notruf2019.Game.RewiredInputController");
                MethodInfo? poll = inputType == null ? null : AccessTools.Method(inputType, "PollInput");
                if (poll != null)
                    playerControlHarmony.Patch(poll, postfix: new HarmonyMethod(typeof(ModMenu), nameof(PlayerPollInputPostfix)));
                MethodInfo? action = inputType == null ? null : AccessTools.Method(inputType, "IsActionKey");
                if (action != null)
                    playerControlHarmony.Patch(action, prefix: new HarmonyMethod(typeof(ModMenu), nameof(PlayerActionKeyPrefix)));
            }
            catch (Exception) { ; }
        }
        // AvatarConfig wird pro Spieler-Avatar geklont, damit NPCs ihre Originalwerte behalten.
        // Originalwerte bleiben erhalten; keine wiederholte Multiplikation.
        private sealed class LocalAvatarSpeedState
        {
            public object Config = null!;
            public float Walk;
            public float Run;
            public float Crouch;
        }
        private static readonly Dictionary<object, LocalAvatarSpeedState> localAvatarSpeedStates =
            new Dictionary<object, LocalAvatarSpeedState>();
        private static float localSpeedLastFactor = -1f;
        private static bool localSpeedWarning;
        private static bool localSpeedFirstLog;
        private static void PlayerAvatarSpeedPostfix(object __instance)
        {
            var menu = playerControlInstance;
            if (menu == null || __instance == null) return;
            try
            {
                object? avatar = AccessTools.Field(__instance.GetType(), "avatar")?.GetValue(__instance);
                if (avatar == null) return;
                Type avatarType = avatar.GetType();
                var controlledProperty = AccessTools.Property(avatarType, "PlayerControlled");
                if (controlledProperty == null || controlledProperty.PropertyType != typeof(bool) ||
                    !(bool)controlledProperty.GetValue(avatar, null)) return;

                FieldInfo configField = AccessTools.Field(avatarType, "config");
                if (configField == null)
                {
                    if (!localSpeedWarning) { localSpeedWarning = true; ; }
                    return;
                }
                object currentConfig = configField.GetValue(avatar);
                if (currentConfig == null) return;
                FieldInfo walkField = AccessTools.Field(currentConfig.GetType(), "walkSpeed");
                FieldInfo runField = AccessTools.Field(currentConfig.GetType(), "runSpeed");
                FieldInfo crouchField = AccessTools.Field(currentConfig.GetType(), "crouchSpeed");
                if (walkField == null || runField == null || walkField.FieldType != typeof(float) || runField.FieldType != typeof(float))
                {
                    if (!localSpeedWarning) { localSpeedWarning = true; ; }
                    return;
                }

                LocalAvatarSpeedState state;
                if (!localAvatarSpeedStates.TryGetValue(avatar, out state) || !ReferenceEquals(state.Config, currentConfig))
                {
                    // Config kann mit anderen Avataren geteilt sein: nur eine eigene Kopie bearbeiten.
                    MethodInfo clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (clone == null) throw new MissingMethodException("MemberwiseClone");
                    object ownConfig = clone.Invoke(currentConfig, null);
                    configField.SetValue(avatar, ownConfig);
                    if (!ReferenceEquals(configField.GetValue(avatar), ownConfig))
                        throw new InvalidOperationException("Avatar.config konnte nicht auf eigene Kopie gesetzt werden");
                    state = new LocalAvatarSpeedState
                    {
                        Config = ownConfig,
                        Walk = (float)walkField.GetValue(ownConfig),
                        Run = (float)runField.GetValue(ownConfig),
                        Crouch = crouchField != null && crouchField.FieldType == typeof(float) ? (float)crouchField.GetValue(ownConfig) : 1f
                    };
                    localAvatarSpeedStates[avatar] = state;
                    ;
                }

                float factor = Mathf.Clamp(menu.playerMovementFactor, 0.5f, 2f);
                float walkTarget = state.Walk * factor;
                float runTarget = state.Run * factor;
                if (!Mathf.Approximately((float)walkField.GetValue(state.Config), walkTarget)) walkField.SetValue(state.Config, walkTarget);
                if (!Mathf.Approximately((float)runField.GetValue(state.Config), runTarget)) runField.SetValue(state.Config, runTarget);
                // crouchSpeed absichtlich nicht veraendert.
                if (!localSpeedFirstLog || Mathf.Abs(localSpeedLastFactor - factor) > 0.02f)
                {
                    localSpeedFirstLog = true;
                    localSpeedLastFactor = factor;
                    ;
                }
            }
            catch (Exception)
            {
                if (!localSpeedWarning) { localSpeedWarning = true; ; }
            }
        }
        private static void PlayerPollInputPostfix(object __instance)
        {
            bool down = Input.GetKey(KeyCode.Tab);
            tabReleaseFrame = tabWasPressed && !down;
            tabWasPressed = down;
            if (!down && !tabReleaseFrame) return;
            try
            {
                // Rewired-Aktionen unterdrücken, die durch TAB ausgelöst werden können.
                var tabs = AccessTools.Field(__instance.GetType(), "tabButtons")?.GetValue(__instance) as System.Collections.IDictionary;
                if (tabs != null)
                    foreach (object key in tabs.Keys)
                        if (tabs[key] is bool b && b) tabs[key] = false;
            }
            catch { }
        }
        private static bool PlayerActionKeyPrefix(ref bool __result)
        {
            if (Input.GetKey(KeyCode.Tab) || tabReleaseFrame) { __result = false; return false; }
            return true;
        }

        private void DrawTrafficTab()
        {
            if (!trafficValuesLoaded && !File.Exists(SettingsFile)) LoadTrafficOptions();
            trafficScroll = GUILayout.BeginScrollView(trafficScroll);
            GUILayout.Label("VERKEHR & FUSSGAENGER", headingStyle);
            GUILayout.Space(10f);
            GUILayout.BeginVertical(panelStyle);
            GUILayout.Label("PKW-DICHTE: " + Mathf.RoundToInt(trafficCars * 100f) + "%", textStyle);
            float cars = GUILayout.HorizontalSlider(trafficCars, 0f, 1f);
            if (Mathf.Abs(cars - trafficCars) > 0.005f) SetTrafficOptions(true, cars);
            GUILayout.Space(12f);
            GUILayout.Label("FUSSGAENGER-DICHTE: " + Mathf.RoundToInt(trafficPedestrians * 100f) + "%", textStyle);
            float pedestrians = GUILayout.HorizontalSlider(trafficPedestrians, 0f, 1f);
            if (Mathf.Abs(pedestrians - trafficPedestrians) > 0.005f) SetTrafficOptions(false, pedestrians);
            GUILayout.Space(10f);
            if (GUILayout.Button("SPIEL-STANDARDWERTE (25% / 30%)", buttonStyle))
            { SetTrafficOptions(true, 0.25f); SetTrafficOptions(false, 0.30f); }
            GUILayout.EndVertical();
            GUILayout.Space(12f);
            GUILayout.BeginVertical(panelStyle);
            trafficYieldEnabled = GUILayout.Toggle(trafficYieldEnabled, "Rettungsgassen-Assistent (experimentell)");
            GUILayout.Label("Erkennt aktive Blaulichtfahrzeuge und gibt Zivilfahrzeugen auf geeigneten mehrspurigen Strassen frueher einen seitlichen Spurwunsch. Nur bei freier Nachbarspur; keine Teleports.", infoStyle);
            GUILayout.Label("Die originale Verkehrs-KI prueft weiterhin den Spurwechsel. Keine Garantie auf eine perfekte Rettungsgasse.", infoStyle);
            GUILayout.EndVertical();
            GUILayout.Space(18f);
            GUILayout.BeginVertical(panelStyle);
            GUILayout.Label("SPIELER-LAUFGESCHWINDIGKEIT: " + playerMovementFactor.ToString("0.00") + "x", headingStyle);
            playerMovementFactor = GUILayout.HorizontalSlider(playerMovementFactor, 0.5f, 2f);
            if (GUILayout.Button("LAUFGESCHWINDIGKEIT ZURUECKSETZEN (1x)", buttonStyle)) playerMovementFactor = 1f;
            GUILayout.Label("Skaliert die echte Bewegungsgeschwindigkeit des eigenen Avatars. KI bleibt unveraendert.", infoStyle);
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void InstallTrafficYieldPatch()
        {
            trafficInstance = this;
            try
            {
                Type? laneType = AccessTools.TypeByName("AILaneChange");
                MethodInfo? calculate = laneType == null ? null : AccessTools.Method(laneType, "CalculateTargetLane");
                if (calculate == null) { ; return; }
                trafficHarmony = new Harmony("de.notruf112.firecontrol.traffic-yield");
                trafficHarmony.Patch(calculate, postfix: new HarmonyMethod(typeof(ModMenu), nameof(TrafficLanePostfix)));
            }
            catch (Exception) { ; }
        }

        private void RefreshTrafficEmergencies()
        {
            if (Time.unscaledTime < nextTrafficEmergencyScan) return;
            nextTrafficEmergencyScan = Time.unscaledTime + 0.6f;
            trafficEmergencyPositions.Clear();
            // Bereits erkannte, aktive Blaulichtcontroller wiederverwenden.
            foreach (BlueLightVehicle v in blueVehicles)
            {
                if (v.Controller == null || v.Vehicle == null) continue;
                try
                {
                    object? raw = blueMaskProperty != null ? blueMaskProperty.GetValue(v.Controller, null) : blueMaskField?.GetValue(v.Controller);
                    if (raw == null || (Convert.ToInt32(raw) & v.EmergencyMask) == 0) continue;
                    trafficEmergencyPositions.Add(v.Vehicle.position);
                }
                catch { }
            }
        }

        // Harmony: nur einen sicheren Spurwunsch vorschlagen, keine Fahrphysik manipulieren.
        private static void TrafficLanePostfix(object[] __args, ref float __result)
        {
            ModMenu? mod = trafficInstance;
            if (mod == null || !mod.trafficYieldEnabled || mod.trafficEmergencyPositions.Count == 0 || __args == null || __args.Length == 0) return;
            try
            {
                object info = __args[0];
                if (Convert.ToBoolean(TrafficMember(info, "EmVehicle") ?? false)) return;
                object? road = TrafficMember(info, "Road");
                if (road == null || Convert.ToBoolean(TrafficMember(road, "isHighway") ?? false)) return;
                object? posRaw = TrafficMember(info, "Position");
                object? dirRaw = TrafficMember(info, "Direction");
                if (!(posRaw is Vector3) || !(dirRaw is Vector3)) return;
                Vector3 pos = (Vector3)posRaw, direction = ((Vector3)dirRaw).normalized;
                float lane = Convert.ToSingle(TrafficMember(info, "Lane") ?? 0f);
                int pathIndex = Convert.ToInt32(TrafficMember(info, "PathIndex") ?? -1);
                if (pathIndex < 0 || Mathf.Abs(lane) < 0.5f) return;
                bool approaching = false;
                for (int i = 0; i < mod.trafficEmergencyPositions.Count; i++)
                {
                    Vector3 offset = mod.trafficEmergencyPositions[i] - pos;
                    if (offset.sqrMagnitude > 2500f || Mathf.Abs(offset.y) > 6f) continue;
                    // Einsatzfahrzeug muss hinter dem Zivil-PKW liegen.
                    float longitudinal = Vector3.Dot(direction, offset);
                    if (longitudinal < -4f && longitudinal > -45f && Vector3.Cross(direction, offset).magnitude < 12f)
                    { approaching = true; break; }
                }
                if (!approaching) return;
                // Einspurig: keine unzulaessigen Ausweichmanöver.
                float target = lane + Mathf.Sign(lane);
                MethodInfo? valid = road.GetType().GetMethod("IsValidLane", TrafficFlags, null, new Type[] { typeof(int), typeof(float) }, null);
                if (valid == null || !Convert.ToBoolean(valid.Invoke(road, new object[] { pathIndex, target }))) return;
                // Sicherheitsabstand zu allen anderen Fahrzeugen auf der Zielspur.
                object? list = TrafficMember(road, "CarList");
                if (!(list is System.Collections.IEnumerable)) return;
                foreach (object other in (System.Collections.IEnumerable)list)
                {
                    if (ReferenceEquals(other, info)) continue;
                    object? otherPos = TrafficMember(other, "Position");
                    object? otherLane = TrafficMember(other, "Lane");
                    if (!(otherPos is Vector3) || otherLane == null) continue;
                    if (Mathf.Abs(Convert.ToSingle(otherLane) - target) < 0.6f && ((Vector3)otherPos - pos).sqrMagnitude < 400f) return;
                }
                __result = target;
            }
            catch { /* Original-KI bei unbekanntem Fahrzeugzustand unveraendert lassen. */ }
        }


        // FIRE CONTROL: dauerhafte Einstellungen, getrennt von Fahrzeughallen.json.
        [DataContract]
        private sealed class PersistentSettings
        {
            [DataMember] public float playerMovementFactor = 1f;
            [DataMember] public int manualBlueCurveMode = 1;
            [DataMember] public float trafficCars = 0.25f;
            [DataMember] public float trafficPedestrians = 0.30f;
            [DataMember] public bool trafficYieldEnabled = true;
            [DataMember] public bool graphicsEnabled = true;
            [DataMember] public bool graphicsAnisotropy;
            [DataMember] public float graphicsReflectionFactor = 1.12f;
            [DataMember] public float[] visualFactors = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
            [DataMember] public bool[] visualActive = { true, true, true, true, true, true, true, true };
            [DataMember] public int mapVisualMode;
            [DataMember] public int mapRenderQuality = 1;
            [DataMember] public int mapRenderSpeed = 1;
            [DataMember] public bool mapFollow = true;
            [DataMember] public bool mapShowAllVehicles = true;
            [DataMember] public bool mapShowActive = true;
            [DataMember] public bool mapShowAvailable = true;
            [DataMember] public bool mapShowVehicleLabels = true;
            [DataMember] public bool mapShowFocusPanel = true;
            [DataMember] public float mapZoom = 250f;
            [DataMember] public string selectedWeather = "";
            [DataMember] public bool hasTimeSpeed;
            [DataMember] public float timeSpeed = 1f;
            [DataMember] public bool hasDateTime;
            [DataMember] public string selectedDateTime = "";
        }

    }
}
