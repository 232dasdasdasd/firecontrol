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

        private void DrawTimeTab()
        {
            timeDateScroll = GUILayout.BeginScrollView(timeDateScroll, false, true);
            GUILayout.Label("ZEIT & DATUM", headingStyle);
            GUILayout.Space(15);

            EnviromentHandler environment = GetEnvironment();

            if (environment != null)
            {
                GUILayout.Label(
                    "Aktuell: " +
                    environment.DateTime.ToString(
                        "dd.MM.yyyy HH:mm:ss"),
                    textStyle);
            }

            if (!dateSelectionInitialized && environment != null)
            {
                DateTime current = environment.DateTime;
                selectedDay = current.Day;
                selectedMonth = current.Month;
                selectedYear = current.Year;
                selectedHour = current.Hour;
                selectedMinute = current.Minute;
                dateSelectionInitialized = true;
            }

            GUILayout.Space(10);
            GUILayout.BeginVertical(panelStyle);
            GUILayout.Label("DATUM EINSTELLEN", headingStyle);
            selectedYear = Mathf.RoundToInt(GUILayout.HorizontalSlider(selectedYear, 2000, 2100));
            GUILayout.Label("JAHR: " + selectedYear, textStyle);
            selectedMonth = Mathf.RoundToInt(GUILayout.HorizontalSlider(selectedMonth, 1, 12));
            GUILayout.Label("MONAT: " + selectedMonth.ToString("00"), textStyle);
            int maxDay = DateTime.DaysInMonth(selectedYear, selectedMonth);
            selectedDay = Mathf.Clamp(selectedDay, 1, maxDay);
            selectedDay = Mathf.RoundToInt(GUILayout.HorizontalSlider(selectedDay, 1, maxDay));
            GUILayout.Label("TAG: " + selectedDay.ToString("00"), textStyle);
            GUILayout.Label("GEWAEHLT: " + selectedDay.ToString("00") + "." + selectedMonth.ToString("00") + "." + selectedYear, textStyle);
            if (GUILayout.Button("DATUM UND UHRZEIT UEBERNEHMEN", activeButtonStyle))
                SetGameDateTime();
            GUILayout.Space(12);
            GUILayout.Label("UHRZEIT EINSTELLEN", headingStyle);
            GUILayout.Label(
                "STUNDE: " + selectedHour.ToString("00"),
                headingStyle);

            selectedHour = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(selectedHour, 0, 23));

            GUILayout.Space(20);

            GUILayout.Label(
                "MINUTE: " + selectedMinute.ToString("00"),
                headingStyle);

            selectedMinute = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(selectedMinute, 0, 59));

            GUILayout.Space(20);

            if (GUILayout.Button(
                "UHRZEIT ÜBERNEHMEN",
                activeButtonStyle))
            {
                SetGameTime(selectedHour, selectedMinute);
            }

            GUILayout.EndVertical();
            GUILayout.Space(20);

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("06:00", buttonStyle))
                SetGameTime(6, 0);

            if (GUILayout.Button("12:00", buttonStyle))
                SetGameTime(12, 0);

            if (GUILayout.Button("18:00", buttonStyle))
                SetGameTime(18, 0);

            if (GUILayout.Button("00:00", buttonStyle))
                SetGameTime(0, 0);

            GUILayout.EndHorizontal();
            GUILayout.Space(18f);
            DrawSpeedTab();
            GUILayout.Space(18f);
            GUILayout.EndScrollView();
        }

        private void DrawWeatherTab()
        {
            GUILayout.Label("WETTERSTEUERUNG", headingStyle);
            GUILayout.Space(15);

            GUILayout.BeginVertical(panelStyle);

            GUILayout.Label("WETTER AUSWÄHLEN", textStyle);
            GUILayout.Label("Rot markiert = zuletzt erfolgreich gewählte Einstellung", infoStyle);
            GUILayout.Space(8);
            WeatherButton("SONNIG / KLAR", "Clear");
            WeatherButton("LEICHT BEWÖLKT", "Scattered");
            WeatherButton("STARK BEWÖLKT", "Overcast");
            WeatherButton("REGEN", "Rainy");
            WeatherButton("STURM", "Storm");

            GUILayout.EndVertical();
        }

        private void WeatherButton(string label, string weather)
        {
            if (GUILayout.Button(label, selectedWeather == weather ? activeButtonStyle : buttonStyle))
                SetGameWeather(weather);
        }

        private void DrawSpeedTab()
        {
            GUILayout.Label("ZEITTEMPO", headingStyle);
            GUILayout.Space(15);

            GUILayout.BeginVertical(panelStyle);

            if (GUILayout.Button("ZEIT ANHALTEN", buttonStyle))
                SetGameSpeed(0f);

            if (GUILayout.Button("NORMAL 1x", buttonStyle))
                SetGameSpeed(1f);

            if (GUILayout.Button("SCHNELL 5x", buttonStyle))
                SetGameSpeed(5f);

            if (GUILayout.Button("SEHR SCHNELL 20x", buttonStyle))
                SetGameSpeed(20f);

            GUILayout.EndVertical();
        }

        private void SetGameDateTime()
        {
            try
            {
                var environment = GetEnvironment();
                if (environment == null || !environment.IsServer)
                {
                    statusMessage = "Datumsteuerung nicht verfügbar (Server erforderlich)";
                    return;
                }
                DateTime selected = new DateTime(selectedYear, selectedMonth, selectedDay,
                    selectedHour, selectedMinute, 0);
                environment.SetDateTime(selected);
                persistent.hasDateTime = true;
                persistent.selectedDateTime = selected.ToString("yyyy-MM-ddTHH:mm:ss");
                SaveAllSettings(true);
                statusMessage = "Datum und Uhrzeit geändert: " + selected.ToString("dd.MM.yyyy HH:mm");
            }
            catch (Exception ex)
            {
                ;
                statusMessage = "Fehler bei der Datumsteuerung: " + ex.Message;
            }
        }

        private void SetGameTime(int hour, int minute)
        {
            try
            {
                var environment = GetEnvironment();

                if (environment == null || !environment.IsServer)
                {
                    statusMessage = "Zeitsteuerung nicht verfügbar";
                    return;
                }

                DateTime newTime = environment.DateTime.Date
                    .AddHours(hour)
                    .AddMinutes(minute);

                environment.SetDateTime(newTime);
                persistent.hasDateTime = true;
                persistent.selectedDateTime = newTime.ToString("yyyy-MM-ddTHH:mm:ss");
                SaveAllSettings(true);

                statusMessage =
                    "Uhrzeit geändert: " + newTime.ToString("HH:mm");
            }
            catch (Exception)
            {
                ;
                statusMessage = "Fehler bei der Zeitsteuerung";
            }
        }

        private void SetGameWeather(string weather)
        {
            try
            {
                var environment = GetEnvironment();

                if (environment == null || !environment.IsServer)
                {
                    statusMessage = "Wettersteuerung nicht verfügbar";
                    return;
                }

                IEnviroment control = environment;
                control.SetWeather(weather);

                selectedWeather = weather;
                SaveAllSettings(true);
                statusMessage = "Wetter gewählt: " + weather;
            }
            catch (Exception)
            {
                ;
                statusMessage = "Fehler bei der Wettersteuerung";
            }
        }

        private void SetGameSpeed(float speed)
        {
            try
            {
                var environment = GetEnvironment();

                if (environment == null || !environment.IsServer)
                {
                    statusMessage = "Zeitsteuerung nicht verfügbar";
                    return;
                }

                environment.SetTimeSpeed(speed);
                persistent.hasTimeSpeed = true;
                persistent.timeSpeed = speed;
                SaveAllSettings(true);
                statusMessage = "Zeittempo: " + speed + "x";
            }
            catch (Exception)
            {
                ;
                statusMessage = "Fehler beim Zeittempo";
            }
        }

        // FAHRZEUGE & PERSONAL: liest nur Daten, ändert keine Spielobjekte.
    }
}
