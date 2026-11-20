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

        private void LoadMissions()
        {
            try
            {
                SituationController controller =
                    GetSituationController();

                if (controller == null)
                {
                    statusMessage = "Keine aktive Spielwelt";
                    return;
                }

                MethodInfo method = typeof(SituationController)
                    .GetMethod(
                        "GetSituationTemplates",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (method == null)
                {
                    statusMessage = "Einsatzliste nicht gefunden";
                    return;
                }

                missionTemplates =
                    method.Invoke(controller, null) as IList;

                selectedMission = -1;

                statusMessage = missionTemplates == null
                    ? "Einsatzliste konnte nicht geladen werden"
                    : missionTemplates.Count + " Einsätze geladen";
            }
            catch (Exception)
            {
                ;
                statusMessage = "Fehler beim Laden der Einsätze";
            }
        }

        private Vector2 missionTabScroll;
        private void DrawMissionsTab()
        {
            missionTabScroll = GUILayout.BeginScrollView(missionTabScroll);
            GUILayout.Label("EINSATZLEITSTELLE", headingStyle);
            GUILayout.Space(8);

            SituationController controller =
                GetSituationController();

            GUILayout.Label(
                controller == null
                    ? "Status: Keine aktive Spielwelt"
                    : controller.IsSituationActive
                        ? "Status: EINSATZ AKTIV"
                        : "Status: EINSATZBEREIT",
                textStyle);

            GUILayout.Space(10);

            GUILayout.BeginHorizontal();

            if (GUILayout.Button(
                "EINSÄTZE LADEN / AKTUALISIEREN",
                activeButtonStyle,
                GUILayout.Width(340)))
            {
                LoadMissions();
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label(
                missionTemplates == null
                    ? "Nicht geladen"
                    : missionTemplates.Count + " Vorlagen",
                textStyle);

            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            GUILayout.Label("EINSATZ SUCHEN", textStyle);

            searchText = GUILayout.TextField(
                searchText,
                GUILayout.Height(32));

            GUILayout.Space(10);

            // Scrollbare Einsatzliste
            missionScroll = GUILayout.BeginScrollView(
                missionScroll,
                GUILayout.Height(270));

            if (missionTemplates != null)
            {
                for (int i = 0; i < missionTemplates.Count; i++)
                {
                    SituationController.SituationTemplate? mission =
                        missionTemplates[i] as
                            SituationController.SituationTemplate;

                    if (mission == null)
                        continue;

                    string label =
                        mission.name + "  |  " +
                        mission.place + "  |  Variante " +
                        mission.variation;

                    if (!string.IsNullOrEmpty(searchText) &&
                        label.IndexOf(
                            searchText,
                            StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    GUIStyle style = selectedMission == i
                        ? activeButtonStyle
                        : listStyle;

                    if (GUILayout.Button(label, style))
                    {
                        selectedMission = i;
                        confirmReset = false;
                    }
                }
            }
            else
            {
                GUILayout.Label(
                    "Klicke auf EINSÄTZE LADEN, um die " +
                    "Originaleinsätze anzuzeigen.",
                    textStyle);
            }

            GUILayout.EndScrollView();

            GUILayout.Space(10);

            if (missionTemplates != null &&
                selectedMission >= 0 &&
                selectedMission < missionTemplates.Count)
            {
                SituationController.SituationTemplate? mission =
                    missionTemplates[selectedMission] as
                        SituationController.SituationTemplate;

                if (mission != null)
                {
                    GUILayout.Label(
                        "AUSGEWÄHLT: " + mission.name,
                        headingStyle);

                    GUILayout.Label(
                        "Ort: " + mission.place +
                        "   |   Variante: " + mission.variation,
                        textStyle);
                }
            }

            GUILayout.Space(10);

            if (GUILayout.Button(
                "AUSGEWÄHLTEN EINSATZ STARTEN",
                activeButtonStyle))
            {
                StartSelectedMission();
            }

            GUILayout.Space(5);

            if (!confirmReset)
            {
                if (GUILayout.Button(
                    "ALLE AKTIVEN EINSÄTZE BEENDEN / ZURÜCKSETZEN",
                    buttonStyle))
                {
                    confirmReset = true;
                }
            }
            else
            {
                GUILayout.Label(
                    "Achtung: Alle laufenden Einsätze werden entfernt!",
                    textStyle);

                GUILayout.BeginHorizontal();

                if (GUILayout.Button(
                    "JA, ALLE ZURÜCKSETZEN",
                    activeButtonStyle))
                {
                    ResetAllMissions();
                    confirmReset = false;
                }

                if (GUILayout.Button(
                    "ABBRECHEN",
                    buttonStyle))
                {
                    confirmReset = false;
                }

                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void StartSelectedMission()
        {
            try
            {
                SituationController controller =
                    GetSituationController();

                if (controller == null ||
                    !controller.IsServer ||
                    SituationController.IsBusy ||
                    controller.IsSituationActive)
                {
                    statusMessage =
                        "Start nicht möglich: Einsatz aktiv " +
                        "oder keine Serverberechtigung";
                    return;
                }

                if (missionTemplates == null ||
                    selectedMission < 0 ||
                    selectedMission >= missionTemplates.Count)
                {
                    statusMessage = "Bitte einen Einsatz auswählen";
                    return;
                }

                SituationController.SituationTemplate? mission =
                    missionTemplates[selectedMission] as
                        SituationController.SituationTemplate;

                if (mission == null)
                    return;

                controller.ReplayServerRpc(
                    mission.name,
                    string.Empty,
                    mission.place,
                    mission.variation);

                statusMessage =
                    "Einsatzstart angefordert: " + mission.name;

                ;
            }
            catch (Exception)
            {
                ;
                statusMessage = "Fehler beim Einsatzstart";
            }
        }

        private void ResetAllMissions()
        {
            try
            {
                SituationController controller =
                    GetSituationController();

                if (controller == null || !controller.IsServer)
                {
                    statusMessage =
                        "Zurücksetzen nur auf dem Server möglich";
                    return;
                }

                if (SituationController.IsBusy)
                {
                    statusMessage =
                        "Bitte warten: Einsatz wird vorbereitet";
                    return;
                }

                controller.ResetToStart();

                statusMessage = "Alle Einsätze zurückgesetzt";
                ;
            }
            catch (Exception)
            {
                ;
                statusMessage = "Fehler beim Zurücksetzen";
            }
        }

    }
}
