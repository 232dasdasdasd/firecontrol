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

        private void LateUpdate()
        {
            KeepCursorFree();
        }

        // Der Spielcode PlayerController.UpdateInput überspringt UpdateAvatar,
        // solange Avatar.IsBusy wahr ist. Während der Torinteraktion wird daher
        // die normale Bewegungsrichtung nicht mehr gesetzt. Wir beheben nur den
        // Bewegungs-Lock des lokalen Avatars bei einer Door/Gateway-Interaktion.
        // Die Tor-Interaktion selbst, ihre Animation und Netzwerkroutinen bleiben unangetastet.
        private void InstallGateMovementFix()
        {
            try
            {
                Type? avatarType = AccessTools.TypeByName("com.crenetic.N2019Plugins.Character.Avatar");
                if (avatarType == null) { ; return; }
                gateInteractionProperty = AccessTools.Property(avatarType, "Interaction");
                gateLocalProperty = AccessTools.Property(avatarType, "IsLocalPlayer");
                gateMovementEnabledField = AccessTools.Field(avatarType, "movementEnabled");
                gateSetDirection = AccessTools.Method(avatarType, "Set", new[] { typeof(float), typeof(float) });
                MethodInfo? isLocked = AccessTools.Method(avatarType, "IsLocked");
                MethodInfo? movement = AccessTools.Method(avatarType, "HandleMovementByPlayer");
                MethodInfo? internals = AccessTools.Method(avatarType, "UpdateInternals");
                if (gateInteractionProperty == null || gateLocalProperty == null || gateMovementEnabledField == null ||
                    gateSetDirection == null || isLocked == null || movement == null || internals == null)
                { ; gateFixEnabled = false; return; }
                gateHarmony = new Harmony("de.notruf112.modmenu.gate-movement");
                gateHarmony.Patch(isLocked, postfix: new HarmonyMethod(typeof(ModMenu), nameof(GateIsLockedPostfix)));
                gateHarmony.Patch(movement, prefix: new HarmonyMethod(typeof(ModMenu), nameof(GateMovementPrefix)));
                gateHarmony.Patch(internals, postfix: new HarmonyMethod(typeof(ModMenu), nameof(GateUpdateInternalsPostfix)));
                ;
            }
            catch (Exception)
            {
                gateFixEnabled = false;
                ;
                try { gateHarmony?.UnpatchSelf(); } catch { }
                gateHarmony = null;
            }
        }

        private static bool IsLocalGateInteraction(object? avatar)
        {
            if (!gateFixEnabled || gateMenuOpen || avatar == null) return false;
            try
            {
                if (!(gateLocalProperty?.GetValue(avatar) is bool isLocal) || !isLocal) return false;
                object? interaction = gateInteractionProperty?.GetValue(avatar);
                if (interaction == null) return false;
                for (Type? type = interaction.GetType(); type != null; type = type.BaseType)
                    if (type.Name == "Door" || type.Name == "Gateway") return true;
            }
            catch { }
            return false;
        }

        private static void GateIsLockedPostfix(object __instance, object lockMode, ref bool __result)
        {
            if (__result && lockMode != null &&
                string.Equals(lockMode.ToString(), "Move", StringComparison.Ordinal) &&
                IsLocalGateInteraction(__instance))
                __result = false;
        }

        private static void GateUpdateInternalsPostfix(object __instance)
        {
            if (IsLocalGateInteraction(__instance))
                gateMovementEnabledField?.SetValue(__instance, true);
        }

        private static void GateMovementPrefix(object __instance)
        {
            if (!IsLocalGateInteraction(__instance) || gateSetDirection == null) return;
            try
            {
                float forward = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f)
                              - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);
                float strafe = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f)
                             - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
                gateSetDirection.Invoke(__instance, new object[] { forward, strafe });
                gateMovementEnabledField?.SetValue(__instance, true);
            }
            catch (Exception)
            {
                if (Time.unscaledTime >= gateNextWarning)
                {
                    gateNextWarning = Time.unscaledTime + 10f;
                    ;
                }
            }
        }

        private void KeepCursorFree()
        {
            if (!menuOpen)
                return;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // Einige Spielcontroller setzen den Cursor nach Update/LateUpdate wieder fest.
        // Deshalb setzen wir den Zustand auch am Ende jedes Frames.
        private IEnumerator CursorUnlockRoutine()
        {
            while (menuOpen)
            {
                yield return new WaitForEndOfFrame();
                KeepCursorFree();
            }
            cursorRoutine = null;
        }

        // Player.Update ruft PlayerController.UpdateInput auf. Solange das Menü
        // offen ist, deaktivieren wir nur diese lokale Player-Komponente.
        // Dadurch bleibt die Maus für das Menü frei, ohne die Kamera zu drehen.
        private void PausePlayerInput()
        {
            if (!menuOpen || pausedPlayerBehaviour != null)
                return;
            try
            {
                Assembly assembly = typeof(SituationController).Assembly;
                Type? playerType = assembly.GetType("com.crenetic.Notruf2019.Network.Player");
                PropertyInfo? localProperty = playerType?.GetProperty("Local",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Behaviour? localPlayer = localProperty?.GetValue(null) as Behaviour;
                if (localPlayer == null)
                    return;

                pausedPlayerBehaviour = localPlayer;
                playerWasEnabled = localPlayer.enabled;
                localPlayer.enabled = false;
            }
            catch (Exception)
            {
                ;
            }
        }

        private void ResumePlayerInput()
        {
            if (pausedPlayerBehaviour == null)
                return;
            try
            {
                if (pausedPlayerBehaviour != null)
                    pausedPlayerBehaviour.enabled = playerWasEnabled;
            }
            catch (Exception)
            {
                ;
            }
            finally
            {
                pausedPlayerBehaviour = null;
            }
        }

        private void OpenMenu()
        {
            cursorWasVisible = Cursor.visible;
            previousCursorLock = Cursor.lockState;

            menuOpen = true;
            PausePlayerInput();
            KeepCursorFree();
            if (cursorRoutine == null)
                cursorRoutine = StartCoroutine(CursorUnlockRoutine());
        }

        private void CloseMenu()
        {
            menuOpen = false;
            ReleaseLiveMap();
            ResumePlayerInput();
            if (cursorRoutine != null)
            {
                StopCoroutine(cursorRoutine);
                cursorRoutine = null;
            }
            Cursor.lockState = previousCursorLock;
            Cursor.visible = cursorWasVisible;
        }

        private Texture2D MakeTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void InitStyles()
        {
            if (titleStyle != null)
                return;

            darkTexture = MakeTexture(
                new Color(0.055f, 0.065f, 0.085f, 0.97f));

            panelTexture = MakeTexture(
                new Color(0.11f, 0.12f, 0.15f, 0.98f));

            redTexture = MakeTexture(
                new Color(0.75f, 0.045f, 0.085f, 1f));

            titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 30;
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.normal.textColor = Color.white;

            headingStyle = new GUIStyle(GUI.skin.label);
            headingStyle.fontSize = 22;
            headingStyle.fontStyle = FontStyle.Bold;
            headingStyle.normal.textColor = Color.white;

            textStyle = new GUIStyle(GUI.skin.label);
            textStyle.fontSize = 16;
            textStyle.normal.textColor =
                new Color(0.82f, 0.84f, 0.88f);

            buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.fontSize = 16;
            buttonStyle.fontStyle = FontStyle.Bold;
            buttonStyle.fixedHeight = 46;
            buttonStyle.normal.background = panelTexture;
            buttonStyle.hover.background = redTexture;
            buttonStyle.active.background = redTexture;
            buttonStyle.normal.textColor = Color.white;
            buttonStyle.hover.textColor = Color.white;
            buttonStyle.active.textColor = Color.white;
            buttonStyle.margin = new RectOffset(4, 4, 5, 5);

            activeButtonStyle = new GUIStyle(buttonStyle);
            activeButtonStyle.normal.background = redTexture;

            closeStyle = new GUIStyle(buttonStyle);
            closeStyle.fixedWidth = 48;

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = panelTexture;
            panelStyle.padding = new RectOffset(16, 16, 12, 12);

            listStyle = new GUIStyle(buttonStyle);
            listStyle.alignment = TextAnchor.MiddleLeft;
            listStyle.fontSize = 14;
            listStyle.fixedHeight = 40;

            // Ein fester Hintergrund fuer alle IMGUI-Zustaende (normal, hover, active).
            windowStyle = new GUIStyle(GUI.skin.window);
            windowStyle.normal.background = darkTexture;
            windowStyle.hover.background = darkTexture;
            windowStyle.active.background = darkTexture;
            windowStyle.focused.background = darkTexture;
            windowStyle.onNormal.background = darkTexture;
            windowStyle.onHover.background = darkTexture;
            windowStyle.onActive.background = darkTexture;
            windowStyle.onFocused.background = darkTexture;

            cursorTexture = MakeTexture(Color.white);
            smallButtonStyle = new GUIStyle(buttonStyle);
            smallButtonStyle.fontSize = 13;
            smallButtonStyle.fixedHeight = 32;
            smallButtonStyle.margin = new RectOffset(2, 2, 2, 2);
            infoStyle = new GUIStyle(textStyle);
            infoStyle.fontSize = 13;
            infoStyle.wordWrap = true;
            imageBadgeStyle = new GUIStyle(headingStyle);
            imageBadgeStyle.alignment = TextAnchor.MiddleCenter;
            imageBadgeStyle.fontSize = 24;
        }

        private EnviromentHandler GetEnvironment()
        {
            return UnityEngine.Object
                .FindFirstObjectByType<EnviromentHandler>();
        }

        private SituationController GetSituationController()
        {
            return UnityEngine.Object
                .FindFirstObjectByType<SituationController>();
        }

        private void DrawWindow(int id)
        {
            GUI.DrawTexture(
                new Rect(0, 0, windowRect.width, 5),
                redTexture);

            GUILayout.BeginArea(
                new Rect(25, 20, windowRect.width - 50, 90));

            GUILayout.BeginHorizontal();

            GUILayout.Label("FIRE CONTROL 3.5.0  |  112", titleStyle);
            GUILayout.FlexibleSpace();

            GUILayout.Label("EINSATZLEITSTELLE", textStyle);

            if (GUILayout.Button("X", closeStyle))
                CloseMenu();

            GUILayout.EndHorizontal();

            GUILayout.Label(
                "NOTRUF 112  /  MOD CONTROL CENTER",
                textStyle);

            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(25, 120, 255, windowRect.height - 185));

            GUILayout.Label("NAVIGATION", textStyle);
            GUILayout.Space(12);

            navigationScroll = GUILayout.BeginScrollView(navigationScroll, GUILayout.Height(Mathf.Max(180f, windowRect.height - 290f)));
            TabButton("DASHBOARD", 0);
            TabButton("EINSÄTZE", 1);
            TabButton("FAHRZEUGE & PERSONAL", 5);
            TabButton("FAHRZEUGHALLEN", 9);
            TabButton("LIVE-KARTE", 6);
            TabButton("ZEIT & DATUM", 2);
            TabButton("WETTER", 3);
            TabButton("EINSTELLUNGEN", 7);
            TabButton("GRAFIK & REFLEXIONEN", 8);
            TabButton("BLAULICHT-KURVEN", 10);
            TabButton("OUTFITS & AUSRUESTUNG", 11);
            GUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            GUILayout.Label("VERSION 3.5.0", textStyle);

            GUILayout.EndArea();

            GUI.DrawTexture(
                new Rect(290, 120, 2, windowRect.height - 185),
                redTexture);

            GUILayout.BeginArea(
                new Rect(315, 120, windowRect.width - 345, windowRect.height - 185));

            switch (selectedTab)
            {
                case 0: DrawOverview(); break;
                case 1: DrawMissionsTab(); break;
                case 2: DrawTimeTab(); break;
                case 3: DrawWeatherTab(); break;
                case 4: DrawTimeTab(); break;
                case 5: DrawVehiclesTab(); break;
                case 6: DrawLiveMapTab(); break;
                case 7: DrawSettingsTab(); break;
                case 8: DrawGraphicsTab(); break;
                case 9: HallDraw(); break;
                case 10: DrawManualBlueCurvesTab(); break;
                case 11: DrawPoliceBundleTab(); break;
            }

            GUILayout.EndArea();

            GUI.Label(
                new Rect(30, windowRect.height - 35, windowRect.width - 60, 25),
                "FIRE CONTROL 3.5.0  |  " + statusMessage,
                textStyle);

            GUI.DragWindow(new Rect(0, 0, windowRect.width - 260, 100));
        }

        private void TabButton(string label, int tab)
        {
            if (GUILayout.Button(
                label,
                selectedTab == tab
                    ? activeButtonStyle
                    : buttonStyle))
            {
                selectedTab = tab;
                if (tab == 0)
                {
                    if (vehicles.Count == 0) RefreshVehicles();
                }
                if (tab == 6)
                {
                    if (vehicles.Count == 0) RefreshVehicles();
                    mapNextRefresh = 0f;
                }
                if (tab == 5)
                {
                    snapshotPreparationRequested = true;
                    snapshotPreparationAttempted = false;
                    if (vehicles.Count == 0) RefreshVehicles();
                }
            }
        }


        // --------------------------------------------------------------------
        // FAHRZEUGHALLEN: ausschließlich editierbare Zusatzplätze A1/A2,
        // B1-B5, C1-C3, D1. Die festen Originalfahrzeuge bleiben unangetastet.
        // Spawn erfolgt nach Sandbox.SpawnUnits beim nächsten Szenenstart.
        // --------------------------------------------------------------------
    }
}
