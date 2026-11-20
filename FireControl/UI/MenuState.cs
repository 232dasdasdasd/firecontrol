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

        private bool menuOpen;
        private Rect windowRect = new Rect(30, 20, 1450, 900);
        private string vehicleSearch = "";
        private string selectedWeather = "";
        private GUIStyle smallButtonStyle = null!;
        private GUIStyle infoStyle = null!;
        private GUIStyle imageBadgeStyle = null!;
        private float nextOriginalPictureCheck;
        private bool snapshotPreparationRequested;
        private bool snapshotPreparationAttempted;
        private int selectedTab;
        private Camera? liveMapCamera;
        private GameObject? liveMapCameraObject;
        private bool liveMapCameraWasActive;
        private bool liveMapCameraWasEnabled;
        private RenderTexture? liveMapOriginalTexture;
        private RenderTexture? liveMapReducedTexture;
        private float mapNextRender;
        private Vector3 mapLastRenderedPosition = new Vector3(float.NaN, 0f, 0f);
        private float mapLastRenderedZoom = -1f;
        private bool mapBackgroundDirty = true;
        private GUIStyle? mapMarkerStyle;
        private object? mapSignAtlas;
        private MethodInfo? mapGetSprite;
        private float mapZoom = 250f;
        private bool mapDragging;
        private readonly Dictionary<string, Sprite?> mapVehicleSigns = new Dictionary<string, Sprite?>();
        private int mapFocusVehicle = -1;
        private bool mapFollow = true;
        private float mapNextRefresh;
        private string mapSearch = "";
        private Vector2 mapListScroll;
        private string mapInfo = "Karte noch nicht geladen";

        // Enhanced map: render the real world without MapView's road-only replacement shader.
        private int mapVisualMode = 0; // 0 Originalkarte, 1 Weltkarte
        private int mapRenderQuality = 1; // 0 low, 1 medium, 2 high
        private int mapRenderSpeed = 1; // 0 eco, 1 balanced, 2 responsive
        private bool mapShowAllVehicles = true;
        private bool mapShowActive = true;
        private bool mapShowAvailable = true;
        private bool mapShowVehicleLabels = true;
        private bool mapShowFocusPanel = true;
        private bool mapCameraStateCaptured;
        private Vector3 mapOriginalCameraPosition;
        private Quaternion mapOriginalCameraRotation;
        private bool mapOriginalOrthographic;
        private float mapOriginalFieldOfView;
        private float mapOriginalFarClip;
        private int mapOriginalCullingMask;
        private Color mapOriginalBackgroundColor;
        private CameraClearFlags mapOriginalClearFlags;
        private bool mapOriginalAllowHDR;
        private Shader? mapReplacementShader;
        private string mapReplacementTag = "MapType";
        private Vector3 mapHomePosition;
        private float mapHomeZoom;
        private float[]? mapOriginalLayerCullDistances;
        private bool mapOriginalLayerCullSpherical;
        private float mapOriginalNearClip;
        private bool cursorWasVisible;
        private CursorLockMode previousCursorLock;

        private int selectedHour = 12;
        private int selectedDay = 1;
        private int selectedMonth = 1;
        private int selectedYear = 2026;
        private bool dateSelectionInitialized;
        private int selectedMinute;

        private string searchText = "";
        private int selectedMission = -1;
        private Vector2 missionScroll;
        private IList? missionTemplates;
        private bool confirmReset;
        private string statusMessage = "Bereit";

        private sealed class VehicleEntry
        {
            public string VehicleName = "";
            public string SquadName = "";
            public string Status = "";
            public string VehicleType = "";
            public GameObject? VehicleObject;
            public Texture? OriginalPicture;
            public readonly List<string> Crew = new List<string>();
            public readonly List<object?> CrewAvatars = new List<object?>();
            public readonly List<string> CrewRoles = new List<string>();
            public readonly List<Sprite?> CrewSigns = new List<Sprite?>();
        }

    }
}
