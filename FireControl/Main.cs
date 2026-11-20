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
    [BepInPlugin("de.notruf112.modmenu", "Fire Control", "3.5.0")]
    public partial class ModMenu : BaseUnityPlugin
    {
        // Hallentor-Fix: ausschließlich für den lokalen Avatar während einer Door/Gateway-Interaktion.
        // Fix ist im laufenden Spiel dauerhaft aktiv. Kein Eingriff in Toranimation oder andere KI.

        private void Awake()
        {
            LoadAllSettings();
            FCLoadingInitialize();
            InstallGateMovementFix();
            InstallTrafficYieldPatch();
            InstallPlayerControlPatches();
            FCInstallPickupTrace();
            ApplyGraphicsTuning();
            HallLoad();
            HallInstallPatch();
            policeNextAutoScan = 0f;
            ;
        }

        // POL_FC: originaler Pulverloescher aus der GameDatabase.
        // Das Prefab wird INAKTIV an seinem Lagerplatz eingehangen und erst dann
        // aktiviert, damit Item.Awake() den korrekten Storage-Transform erfasst.
        // Weltgegenstand statt unvollstaendigem Fahrzeug-Storage.
        // Der Anker bleibt am Auto, die Item-Instanz bleibt auf Root-Ebene.
        private void Update()
        {
            FCUpdateRealPolice();
            FCLoadingUpdate();
            FCInstallOriginalExtinguisher();
            // Native Item.Storage / Item.OnItemExchange erledigen die Rueckgabe.
            // Keine kuenstliche Parenting- oder Snap-Logik mehr!
            FCUpdatePoliceGear();
            FCScanAutoLights();

            gateMenuOpen = menuOpen;
            if (!visualAutoCaptured && Time.unscaledTime >= visualAutoCaptureAt)
            {
                visualAutoCaptured = true;
                VisualCaptureLights(); // Genau einmal beim Start; kein Dauerscan.
            }
            // Keine periodische Blaulicht-Suche oder Helligkeitsmanipulation.
            // Lichtkurven werden nur auf ausdrueckliche Menueaktion geaendert.
            if (Time.unscaledTime >= settingsNextCheck)
            {
                settingsNextCheck = Time.unscaledTime + 1f;
                SaveAllSettings();
                RestoreEnvironmentSettings();
            }
            RefreshTrafficEmergencies();
            // Polizei-Ausrüstung: automatischer Scan deaktiviert.
            // Outfitwechsel zeitnah pruefen, ohne die ganze Welt erneut zu scannen.
            if (Time.unscaledTime >= serviceRadioNextOutfitCheck)
            {
                serviceRadioNextOutfitCheck = Time.unscaledTime + 0.75f;
                // Alte Bundle-Funkgeräte vorläufig deaktiviert.
            }
            // Keine Material-/Mesh-Diagnose fuer die alte Polizei-Ausrüstung.
            if (Input.GetKeyDown(KeyCode.F6))
            {
                if (menuOpen)
                    CloseMenu();
                else
                    OpenMenu();
            }

            KeepCursorFree();
            PausePlayerInput();
            if (menuOpen && selectedTab == 6)
                UpdateLiveMap();
            else if (liveMapCamera != null)
                ReleaseLiveMap();
            if (menuOpen && selectedTab == 5 && Time.unscaledTime >= nextOriginalPictureCheck)
            {
                nextOriginalPictureCheck = Time.unscaledTime + 2f;
                if (snapshotPreparationRequested && !snapshotPreparationAttempted)
                {
                    snapshotPreparationAttempted = true;
                    PrepareOriginalVehiclePictures();
                }
                RefreshOriginalVehiclePictures();
                RefreshOriginalCrewSigns();
            }
        }

        private void OnDisable()
        {
            FCLoadingShutdown();
            FCCleanupPoliceGear();
            FCRemoveRealPolice();
            if (fcVehicleBundle != null)
            {
                fcVehicleBundle.Unload(false);
                fcVehicleBundle = null;
                fcVehiclePrefab = null;
            }
            SaveAllSettings(true);
            HallUnpatch();
            RestoreGraphicsTuning();
            VisualRestoreLights();
            RestoreExistingBlueLights();
            RestoreManualBlueCurves();
            RemovePoliceTestInstances();
            foreach (var pair in policeAutoInstances) if (pair.Value != null) Destroy(pair.Value);
            policeAutoInstances.Clear();
            foreach (var pair in serviceRadioInstances) if (pair.Value != null) Destroy(pair.Value);
            serviceRadioInstances.Clear();
            serviceRadioKinds.Clear();
            if (policeBundle != null) { policeBundle.Unload(false); policeBundle = null; policeEquipmentPrefab = null; }
            if (policeAvatarBundle != null && policeAvatarBundleOwned) policeAvatarBundle.Unload(false);
            policeAvatarBundle = null;
            policeAvatarBundleOwned = false;
            blueVehicles.Clear();
            if (menuOpen)
                CloseMenu();
            gateFixEnabled = false;
            try { gateHarmony?.UnpatchSelf(); } catch { }
            gateHarmony = null;
            try { fcPickupTraceHarmony?.UnpatchSelf(); } catch { }
            fcPickupTraceHarmony = null;
            if (fcPickupTraceInstance == this) fcPickupTraceInstance = null;
            try { playerControlHarmony?.UnpatchSelf(); } catch { }
            playerControlHarmony = null;
            if (playerControlInstance == this) playerControlInstance = null;
            try { trafficHarmony?.UnpatchSelf(); } catch { }
            trafficHarmony = null;
            if (trafficInstance == this) trafficInstance = null;
        }

        private void OnGUI()
        {
            if (!menuOpen)
                return;

            InitStyles();

            // Groesseres Fenster, aber auch auf kleineren Aufloesungen voll bedienbar.
            float width = Mathf.Min(1780f, Mathf.Max(680f, Screen.width - 24f));
            float height = Mathf.Min(1040f, Mathf.Max(540f, Screen.height - 24f));
            windowRect.width = width;
            windowRect.height = height;
            windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, Screen.width - width));
            windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, Screen.height - height));

            // Spielseitige GUI-Farben duerfen unser Fenster nicht aufhellen.
            Color oldColor = GUI.color;
            Color oldBackgroundColor = GUI.backgroundColor;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.DrawTexture(windowRect, darkTexture, ScaleMode.StretchToFill);
            windowRect = GUI.Window(
                112,
                windowRect,
                DrawWindow,
                "",
                windowStyle);
            GUI.color = oldColor;
            GUI.backgroundColor = oldBackgroundColor;

            // Sichtbarer Fallback-Zeiger, falls das Spiel den Systemcursor versteckt.
            // Der echte Unity-Cursor bleibt ebenfalls entsperrt.
            if (Event.current.type == EventType.Repaint)
            {
                Vector3 mouse = Input.mousePosition;
                Rect pointer = new Rect(mouse.x, Screen.height - mouse.y, 12f, 17f);
                GUI.DrawTexture(pointer, cursorTexture);
            }
            KeepCursorFree();
        }

    }
}
