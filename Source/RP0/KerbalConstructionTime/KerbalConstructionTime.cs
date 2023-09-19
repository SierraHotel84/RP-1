﻿using KSP.UI.Screens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UniLinq;
using ToolbarControl_NS;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UI;
using RP0.UI;

namespace RP0
{
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class KerbalConstructionTime : MonoBehaviour
    {
        #region Statics

        // Per saveslot values
        public static bool ErroredDuringOnLoad = false;
        public static bool VesselErrorAlerted = false;

        public static KCTSettings Settings = new KCTSettings();

        public static bool EditorShipEditingMode = false;
        public static double EditorRolloutCost = 0;
        public static double EditorRolloutBP = 0;
        public static double EditorUnlockCosts = 0;
        public static double EditorToolingCosts = 0;
        public static List<string> EditorRequiredTechs = new List<string>();

        public static List<bool> ShowWindows = new List<bool> { false, true };    //build list, editor
        

        public static void Reset()
        {
            VesselErrorAlerted = false;

            KCT_GUI.ResetFormulaRateHolders();
            KCT_GUI.ResetShowFirstRunAgain();
        }

        public static void ClearVesselEditMode()
        {
            EditorShipEditingMode = false;
            KerbalConstructionTimeData.Instance.EditedVessel = new VesselProject();
            KerbalConstructionTimeData.Instance.MergedVessels.Clear();

            InputLockManager.RemoveControlLock("KCTEditExit");
            InputLockManager.RemoveControlLock("KCTEditLoad");
            InputLockManager.RemoveControlLock("KCTEditNew");
            InputLockManager.RemoveControlLock("KCTEditLaunch");
            EditorLogic.fetch?.Unlock("KCTEditorMouseLock");
        }
        
        #endregion

        public static KerbalConstructionTime Instance { get; private set; }

        private Button.ButtonClickedEvent _recoverCallback, _flyCallback;
        private SpaceTracking _trackingStation;

        public bool IsEditorRecalcuationRequired = false;
        private bool _hasFirstRecalculated = false;

        private static bool _isGUIInitialized = false;

        private WaitForSeconds _wfsHalf = null, _wfsOne = null, _wfsTwo = null;
        private double _lastRateUpdateUT = 0;
        private double _lastYearMultUpdateUT = 0;

        internal const string KCTLaunchLock = "KCTLaunchLock";
        internal const string KCTKSCLock = "KCTKSCLock";
        private const float BUILD_TIME_INTERVAL = 0.5f;
        private const float YEAR_MULT_TIME_INTERVAL = 86400 * 7;

        // Editor fields
        public VesselProject EditorVessel = new VesselProject("temp", "LaunchPad", 0d, 0d, 0d, string.Empty, 0f, 0f, EditorFacility.VAB, false);
        public Guid PreEditorSwapLCID = Guid.Empty;
        public bool IsLaunchSiteControllerDisabled;

        private DateTime _simMoveDeferTime = DateTime.MaxValue;
        private int _simMoveSecondsRemain = 0;

        private GameObject _simWatermark;

        public void OnDestroy()
        {
            _simWatermark?.DestroyGameObject();

            if (KerbalConstructionTimeData.ToolbarControl != null)
            {
                KerbalConstructionTimeData.ToolbarControl.OnDestroy();
                Destroy(KerbalConstructionTimeData.ToolbarControl);
            }
            KCT_GUI.ClearTooltips();
            KCT_GUI.OnDestroy();
            
            Instance = null;
        }

        internal void OnGUI()
        {
            if (KCTUtilities.CurrentGameIsMission()) return;

            if (!_isGUIInitialized)
            {
                KCT_GUI.InitBuildListVars();
                _isGUIInitialized = true;
            }
            KCT_GUI.SetGUIPositions();
        }

        public void Awake()
        {
            if (KCTUtilities.CurrentGameIsMission()) return;

            RP0Debug.Log("Awake called");

            if (Instance != null)
                Destroy(Instance);

            Instance = this;

            Settings.Load();

            if (PresetManager.Instance == null)
            {
                PresetManager.Instance = new PresetManager();
            }
            PresetManager.Instance.SetActiveFromSaveData();

            // Create events for other mods
            if (!KCTEvents.Instance.CreatedEvents)
            {
                KCTEvents.Instance.CreateEvents();
            }

            var obj = new GameObject("KCTToolbarControl");
            KerbalConstructionTimeData.ToolbarControl = obj.AddComponent<ToolbarControl>();
            KerbalConstructionTimeData.ToolbarControl.AddToAllToolbars(null, null,
                null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.TRACKSTATION | ApplicationLauncher.AppScenes.VAB,
                KerbalConstructionTimeData._modId,
                "MainButton",
                KCTUtilities._icon_KCT_On_38,
                KCTUtilities._icon_KCT_Off_38,
                KCTUtilities._icon_KCT_On_24,
                KCTUtilities._icon_KCT_Off_24,
                KerbalConstructionTimeData._modName
                );

            KerbalConstructionTimeData.ToolbarControl.AddLeftRightClickCallbacks(KCT_GUI.ClickToggle, KCT_GUI.OnRightClick);
        }

        public void Start()
        {
            RP0Debug.Log("Start called");
            _wfsOne = new WaitForSeconds(1f);
            _wfsTwo = new WaitForSeconds(2f);
            _wfsHalf = new WaitForSeconds(0.5f);

            KCT_GUI.InitTooltips();

            if (KCTUtilities.CurrentGameIsMission()) return;

            // Subscribe to events from KSP and other mods
            if (!KCTEvents.Instance.SubscribedToEvents)
            {
                KCTEvents.Instance.SubscribeToEvents();
            }

            if (KerbalConstructionTimeData.Instance.IsFirstStart)
            {
                PresetManager.Instance.SaveActiveToSaveData();
            }

            // Ghetto event queue
            if (HighLogic.LoadedScene == GameScenes.EDITOR)
            {
                KCT_GUI.BuildRateForDisplay = null;
                if (!KCT_GUI.IsPrimarilyDisabled)
                {
                    IsEditorRecalcuationRequired = true;
                }
                InvokeRepeating("EditorRecalculation", 0.02f, 1f);
            }

            if (KCT_GUI.IsPrimarilyDisabled &&
                InputLockManager.GetControlLock(KCTLaunchLock) == ControlTypes.EDITOR_LAUNCH)
            {
                InputLockManager.RemoveControlLock(KCTLaunchLock);
            }

            KACWrapper.InitKACWrapper();

            if (!PresetManager.Instance.ActivePreset.GeneralSettings.Enabled)
            {
                if (InputLockManager.GetControlLock(KCTKSCLock) == ControlTypes.KSC_FACILITIES)
                    InputLockManager.RemoveControlLock(KCTKSCLock);
                return;
            }

            //Begin primary mod functions

            KCT_GUI.GuiDataSaver.Load();
            KCT_GUI.GUIStates.HideAllNonMainWindows();

            if (!HighLogic.LoadedSceneIsFlight)
            {
                KerbalConstructionTimeData.Instance.SimulationParams.Reset();
            }

            switch (HighLogic.LoadedScene)
            {
                case GameScenes.EDITOR:
                    KCT_GUI.HideAll();
                    if (!KCT_GUI.IsPrimarilyDisabled)
                    {
                        KCT_GUI.GUIStates.ShowEditorGUI = ShowWindows[1];
                        if (EditorShipEditingMode)
                            KCT_GUI.EnsureEditModeIsVisible();
                        else
                            KCT_GUI.ToggleVisibility(KCT_GUI.GUIStates.ShowEditorGUI);
                    }
                    break;
                case GameScenes.SPACECENTER:
                    bool shouldStart = KCT_GUI.GUIStates.ShowFirstRun;
                    KCT_GUI.HideAll();
                    ClearVesselEditMode();
                    if (!shouldStart)
                    {
                        KCT_GUI.GUIStates.ShowBuildList = ShowWindows[0];
                        KCT_GUI.ToggleVisibility(KCT_GUI.GUIStates.ShowBuildList);
                    }
                    KCT_GUI.GUIStates.ShowFirstRun = shouldStart;
                    StartCoroutine(UpdateFacilityLevels());
                    break;
                case GameScenes.TRACKSTATION:
                    ClearVesselEditMode();
                    break;
                case GameScenes.FLIGHT:
                    KCT_GUI.HideAll();
                    ProcessFlightStart();
                    break;
            }

            RP0Debug.Log("Start finished");

            DelayedStart();

            StartCoroutine(HandleEditorButton_Coroutine());

            if (HighLogic.LoadedScene == GameScenes.TRACKSTATION)
            {
                _trackingStation = FindObjectOfType<SpaceTracking>();
                if (_trackingStation != null)
                {
                    _recoverCallback = _trackingStation.RecoverButton.onClick;
                    _flyCallback = _trackingStation.FlyButton.onClick;

                    _trackingStation.RecoverButton.onClick = new Button.ButtonClickedEvent();
                    _trackingStation.RecoverButton.onClick.AddListener(RecoveryChoiceTS);
                }
            }
            else if (HighLogic.LoadedSceneIsFlight)
            {
                if (FindObjectOfType<AltimeterSliderButtons>() is AltimeterSliderButtons altimeter)
                {
                    _recoverCallback = altimeter.vesselRecoveryButton.onClick;

                    altimeter.vesselRecoveryButton.onClick = new Button.ButtonClickedEvent();
                    altimeter.vesselRecoveryButton.onClick.AddListener(RecoveryChoiceFlight);
                }
            }
        }

        private void ProcessFlightStart()
        {
            if (FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.situation != Vessel.Situations.PRELAUNCH) return;

            VesselProject blv = KerbalConstructionTimeData.Instance.LaunchedVessel;
            var dataModule = (KCTVesselTracker)FlightGlobals.ActiveVessel.vesselModules.Find(vm => vm is KCTVesselTracker);
            if (dataModule != null)
            {
                if (string.IsNullOrWhiteSpace(dataModule.Data.LaunchID))
                {
                    dataModule.Data.LaunchID = Guid.NewGuid().ToString("N");
                    RP0Debug.Log($"Assigned LaunchID: {dataModule.Data.LaunchID}");
                }

                // This will only fire the first time, because we make it invalid afterwards by clearing the BLV
                if (blv.IsValid)
                {
                    dataModule.Data.FacilityBuiltIn = blv.FacilityBuiltIn;
                    dataModule.Data.VesselID = blv.KCTPersistentID;
                    dataModule.Data.LCID = blv.LCID;
                    if (dataModule.Data.LCID != Guid.Empty)
                        dataModule.Data.LCModID = blv.LC.ModID;
                }
            }

            if (KCT_GUI.IsPrimarilyDisabled) return;

            AssignCrewToCurrentVessel();

            // This only fires the first time because we clear the BLV afterwards.
            if (blv.IsValid)
            {
                LaunchComplex vesselLC = blv.LC;
                RP0Debug.Log("Attempting to remove launched vessel from build list");
                if (blv.RemoveFromBuildList(out _)) //Only do these when the vessel is first removed from the list
                {
                    //Add the cost of the ship to the funds so it can be removed again by KSP
                    FlightGlobals.ActiveVessel.vesselName = blv.shipName;
                }
                if (vesselLC == null) vesselLC = KerbalConstructionTimeData.Instance.ActiveSC.ActiveLC;
                if (vesselLC.Recon_Rollout.FirstOrDefault(r => r.associatedID == blv.shipID.ToString()) is ReconRolloutProject rollout)
                    vesselLC.Recon_Rollout.Remove(rollout);

                if (vesselLC.Airlaunch_Prep.FirstOrDefault(r => r.associatedID == blv.shipID.ToString()) is AirlaunchProject alPrep)
                    vesselLC.Airlaunch_Prep.Remove(alPrep);

                KerbalConstructionTimeData.Instance.LaunchedVessel = new VesselProject();
            }

            var alParams = KerbalConstructionTimeData.Instance.AirlaunchParams;
            if ((blv.IsValid && alParams.KCTVesselId == blv.shipID) ||
                alParams.KSPVesselId == FlightGlobals.ActiveVessel.id)
            {
                if (alParams.KSPVesselId == Guid.Empty)
                    alParams.KSPVesselId = FlightGlobals.ActiveVessel.id;
                StartCoroutine(AirlaunchRoutine(alParams, FlightGlobals.ActiveVessel.id));

                // Clear the KCT vessel ID but keep KSP's own ID.
                // 'Revert To Launch' state is saved some frames after the scene got loaded so KerbalConstructionTimeData.Instance.LaunchedVessel is no longer there.
                // In this case we use KSP's own id to figure out if airlaunch should be done.
                KerbalConstructionTimeData.Instance.AirlaunchParams.KCTVesselId = Guid.Empty;
            }
        }

        private static void AssignCrewToCurrentVessel()
        {
            if (!KerbalConstructionTimeData.Instance.IsSimulatedFlight &&
                FlightGlobals.ActiveVessel.GetCrewCount() == 0 && KerbalConstructionTimeData.Instance.LaunchedCrew.Count > 0)
            {
                KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
                foreach (Part p in FlightGlobals.ActiveVessel.parts)
                {
                    RP0Debug.Log($"Part being tested: {p.partInfo.title}");
                    if (p.CrewCapacity == 0 || !(KerbalConstructionTimeData.Instance.LaunchedCrew.Find(part => part.PartID == p.craftID) is PartCrewAssignment cp))
                        continue;
                    List<CrewMemberAssignment> crewList = cp.CrewList;
                    RP0Debug.Log($"cP.crewList.Count: {cp.CrewList.Count}");
                    foreach (CrewMemberAssignment assign in crewList)
                    {
                        ProtoCrewMember crewMember = assign.PCM;
                        if (crewMember == null)
                            continue;

                        try
                        {
                            if (p.AddCrewmember(crewMember))
                            {
                                RP0Debug.Log($"Assigned {crewMember.name} to {p.partInfo.name}");
                                crewMember.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                                crewMember.seat?.SpawnCrew();
                            }
                            else
                            {
                                RP0Debug.LogError($"Error when assigning {crewMember.name} to {p.partInfo.name}");
                                crewMember.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                            }
                        }
                        catch (Exception ex)
                        {
                            RP0Debug.LogError($"Error when assigning {crewMember.name} to {p.partInfo.name}: {ex}");
                            crewMember.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        }
                    }
                }
                KerbalConstructionTimeData.Instance.LaunchedCrew.Clear();
            }
        }

        internal IEnumerator AirlaunchRoutine(AirlaunchParams launchParams, Guid vesselId, bool skipCountdown = false)
        {
            if (!skipCountdown)
                yield return _wfsTwo;

            for (int i = 10; i > 0 && !skipCountdown; i--)
            {
                if (FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.id != vesselId)
                {
                    ScreenMessages.PostScreenMessage("[KCT] Airlaunch cancelled", 5f, ScreenMessageStyle.UPPER_CENTER, XKCDColors.Red);
                    yield break;
                }

                if (i == 1 && FlightGlobals.ActiveVessel.situation == Vessel.Situations.PRELAUNCH)
                {
                    // Make sure that the vessel situation transitions from Prelaunch to Landed before airlaunching
                    FlightGlobals.ActiveVessel.situation = Vessel.Situations.LANDED;
                }

                ScreenMessages.PostScreenMessage($"[KCT] Launching in {i}...", 1f, ScreenMessageStyle.UPPER_CENTER, XKCDColors.Red);
                yield return _wfsOne;
            }

            HyperEdit_Utilities.DoAirlaunch(launchParams);

            if (KCTUtilities.IsPrincipiaInstalled)
                StartCoroutine(ClobberPrincipia());
        }

        /// <summary>
        /// Need to keep the vessel in Prelaunch state for a while if Principia is installed.
        /// Otherwise the vessel will spin out in a random way.
        /// </summary>
        /// <returns></returns>
        private IEnumerator ClobberPrincipia()
        {
            if (FlightGlobals.ActiveVessel == null)
                yield return null;

            const int maxFramesWaited = 250;
            int i = 0;
            do
            {
                FlightGlobals.ActiveVessel.situation = Vessel.Situations.PRELAUNCH;
                yield return new WaitForFixedUpdate();
            } while (FlightGlobals.ActiveVessel.packed && i++ < maxFramesWaited);
            // Need to fire this so trip logger etc notice we're flying now.
            RP0Debug.Log($"Finished clobbering vessel situation of {FlightGlobals.ActiveVessel.name} to PRELAUNCH (for Prinicipia stability), now firing change event to FLYING.");
            FlightGlobals.ActiveVessel.situation = Vessel.Situations.FLYING;
            GameEvents.onVesselSituationChange.Fire(new GameEvents.HostedFromToAction<Vessel, Vessel.Situations>(FlightGlobals.ActiveVessel, Vessel.Situations.PRELAUNCH, Vessel.Situations.FLYING));
        }

        protected void EditorRecalculation()
        {
            if (IsEditorRecalcuationRequired)
            {
                if (EditorDriver.fetch != null && !EditorDriver.fetch.restartingEditor)
                {
                    _hasFirstRecalculated = true;
                    IsEditorRecalcuationRequired = false;
                    KCTUtilities.RecalculateEditorBuildTime(EditorLogic.fetch.ship);
                }
                // make sure we're not destructing
                else if (!_hasFirstRecalculated && this != null)
                {
                    StartCoroutine(CallbackUtil.DelayedCallback(0.02f, EditorRecalculation));
                }
            }
        }

        /// <summary>
        /// Coroutine to reset the launch button handlers every 1/2 second
        /// Needed because KSP seems to change them behind the scene sometimes
        /// </summary>
        /// <returns></returns>
        IEnumerator HandleEditorButton_Coroutine()
        {
            while (true)
            {
                if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null)
                    KCTUtilities.HandleEditorButton();
                yield return _wfsHalf;
            }
        }

        public void FixedUpdate()
        {
            if (KCTUtilities.CurrentGameIsMission()) return;
            if (!PresetManager.Instance?.ActivePreset?.GeneralSettings.Enabled == true)
                return;
            double UT = Planetarium.GetUniversalTime();
            if (_lastRateUpdateUT == 0d)
                _lastRateUpdateUT = UT;
            double UTDiff = UT - _lastRateUpdateUT;
            if (!KCT_GUI.IsPrimarilyDisabled && (TimeWarp.CurrentRateIndex > 0 || UTDiff > BUILD_TIME_INTERVAL))
            {
                // Drive this from RP-1: ProgressBuildTime(UTDiff);
                _lastRateUpdateUT = UT;

                if (UT - _lastYearMultUpdateUT > YEAR_MULT_TIME_INTERVAL)
                {
                    UpdateTechYearMults();
                    _lastYearMultUpdateUT = UT;
                }
            }

            if (HighLogic.LoadedSceneIsFlight && KerbalConstructionTimeData.Instance.IsSimulatedFlight)
            {
                ProcessSimulation();
            }
        }

        // Ran every 30 FixedUpdates, which we will treat as 0.5 seconds for now.
        // First we update locked buildings, then we loop on pad.
        // FIXME we could do this on event, but sometimes things get hinky.
        private IEnumerator UpdateFacilityLevels()
        {
            // Only run during Space Center in career mode
            // Also need to wait a bunch of frames until KSP has initialized Upgradable and Destructible facilities
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            if (HighLogic.LoadedScene != GameScenes.SPACECENTER || !KCTUtilities.CurrentGameIsCareer())
                yield break;

            FacilityUpgradeProject.UpgradeLockedFacilities();

            while (HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                if (KerbalConstructionTimeData.Instance?.ActiveSC.ActiveLC.ActiveLPInstance is LCLaunchPad pad)
                {
                    if (KCTUtilities.GetBuildingUpgradeLevel(SpaceCenterFacility.LaunchPad) != pad.level)
                    {
                        KerbalConstructionTimeData.Instance.ActiveSC.ActiveLC.SwitchLaunchPad(KerbalConstructionTimeData.Instance.ActiveSC.ActiveLC.ActiveLaunchPadIndex, false);
                        pad.UpdateLaunchpadDestructionState(false);
                    }
                }
                yield return _wfsHalf;
            }
        }

        private void ProcessSimulation()
        {
            HighLogic.CurrentGame.Parameters.Flight.CanAutoSave = false;

            SimulationParams simParams = KerbalConstructionTimeData.Instance.SimulationParams;
            if (FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed && !simParams.IsVesselMoved)
            {
                if (simParams.DisableFailures)
                {
                    KCTUtilities.ToggleFailures(!simParams.DisableFailures);
                }
                if (!simParams.SimulateInOrbit || !FlightDriver.CanRevertToPrelaunch)
                {
                    // Either the player does not want to start in orbit or they saved and then loaded back into that save
                    simParams.IsVesselMoved = true;
                    return;
                }

                int secondsForMove = simParams.DelayMoveSeconds;
                if (_simMoveDeferTime == DateTime.MaxValue)
                {
                    _simMoveDeferTime = DateTime.Now;
                }
                else if (DateTime.Now.CompareTo(_simMoveDeferTime.AddSeconds(secondsForMove)) > 0)
                {
                    StartCoroutine(SetSimOrbit(simParams));
                    simParams.IsVesselMoved = true;
                    _simMoveDeferTime = DateTime.MaxValue;
                }

                if (_simMoveDeferTime != DateTime.MaxValue && _simMoveSecondsRemain != (_simMoveDeferTime.AddSeconds(secondsForMove) - DateTime.Now).Seconds)
                {
                    double remaining = (_simMoveDeferTime.AddSeconds(secondsForMove) - DateTime.Now).TotalSeconds;
                    ScreenMessages.PostScreenMessage($"Moving vessel in {Math.Round(remaining)} seconds", (float)(remaining - Math.Floor(remaining)), ScreenMessageStyle.UPPER_CENTER);
                    _simMoveSecondsRemain = (int)remaining;
                }
            }
        }

        private static IEnumerator SetSimOrbit(SimulationParams simParams)
        {
            yield return new WaitForEndOfFrame();
            RP0Debug.Log($"Moving vessel to orbit. {simParams.SimulationBody.bodyName}:{simParams.SimOrbitAltitude}:{simParams.SimInclination}");
            HyperEdit_Utilities.PutInOrbitAround(simParams.SimulationBody, simParams.SimOrbitAltitude, simParams.SimInclination);
        }

        private void AddSimulationWatermark()
        {
            if (!Settings.ShowSimWatermark) return;

            var uiController = KSP.UI.UIMasterController.Instance;
            if (uiController == null)
            {
                RP0Debug.LogError("UIMasterController.Instance is null");
                return;
            }

            _simWatermark = new GameObject();
            _simWatermark.transform.SetParent(uiController.mainCanvas.transform, false);
            _simWatermark.name = "sim-watermark";

            var c = Color.gray;
            c.a = 0.65f;
            var text = _simWatermark.AddComponent<Text>();
            text.text = "Simulation";
            text.font = UISkinManager.defaultSkin.font;
            text.fontSize = (int)(40 * uiController.uiScale);
            text.color = c;
            text.alignment = TextAnchor.MiddleCenter;

            var rectTransform = text.GetComponent<RectTransform>();
            rectTransform.localPosition = Vector3.zero;
            rectTransform.localScale = Vector3.one;
            rectTransform.anchorMin = rectTransform.anchorMax = new Vector2(0.5f, 0.85f);
            rectTransform.sizeDelta = new Vector2(190 * uiController.uiScale, 50 * uiController.uiScale);

            if (DateTime.Today.Month == 4 && DateTime.Today.Day == 1)
            {
                text.text = "Activate Windows";
                rectTransform.anchorMin = rectTransform.anchorMax = new Vector2(0.8f, 0.2f);
                rectTransform.sizeDelta = new Vector2(300 * uiController.uiScale, 50 * uiController.uiScale);
            }
        }

        public void ProgressBuildTime(double UTDiff)
        {
            Profiler.BeginSample("RP0ProgressBuildTime");

            if (UTDiff > 0)
            {
                int passes = 1;
                double remainingUT = UTDiff;
                if (remainingUT > 86400d)
                {
                    passes = (int)(UTDiff / 86400d);
                    remainingUT = UTDiff - passes * 86400d;
                    ++passes;
                }
                int rushingEngs = 0;

                int totalEngineers = 0;
                foreach (SpaceCenter ksc in KerbalConstructionTimeData.Instance.KSCs)
                {
                    totalEngineers += ksc.Engineers;

                    for (int j = ksc.LaunchComplexes.Count - 1; j >= 0; j--)
                    {
                        LaunchComplex currentLC = ksc.LaunchComplexes[j];
                        if (!currentLC.IsOperational || currentLC.Engineers == 0 || !currentLC.IsActive)
                            continue;

                        double portionEngineers = currentLC.Engineers / (double)currentLC.MaxEngineers;

                        if (currentLC.IsRushing)
                            rushingEngs += currentLC.Engineers;
                        else
                        {
                            for (int p = 0; p < passes; ++p)
                            {
                                double timestep = p == 0 ? remainingUT : 86400d;
                                currentLC.EfficiencySource?.IncreaseEfficiency(timestep, portionEngineers);
                            }
                        }

                        double timeForBuild = UTDiff;
                        while(timeForBuild > 0d && currentLC.BuildList.Count > 0)
                        {
                            timeForBuild = currentLC.BuildList[0].IncrementProgress(UTDiff);
                        }

                        for (int i = currentLC.Recon_Rollout.Count; i-- > 0;)
                        {
                            // These work in parallel so no need to track excess time
                            var rr = currentLC.Recon_Rollout[i];
                            rr.IncrementProgress(UTDiff);
                            //Reset the associated launchpad id when rollback completes
                            Profiler.BeginSample("RP0ProgressBuildTime.ReconRollout.FindBLVesselByID");
                            if (rr.RRType == ReconRolloutProject.RolloutReconType.Rollback && rr.IsComplete()
                                && KCTUtilities.FindBLVesselByID(rr.LC, new Guid(rr.associatedID)) is VesselProject blv)
                            {
                                blv.launchSiteIndex = -1;
                            }
                            Profiler.EndSample();
                        }

                        currentLC.Recon_Rollout.RemoveAll(rr => rr.RRType != ReconRolloutProject.RolloutReconType.Rollout && rr.IsComplete());
                        
                        // These also are in parallel
                        for (int i = currentLC.Airlaunch_Prep.Count; i-- > 0;)
                            currentLC.Airlaunch_Prep[i].IncrementProgress(UTDiff);

                        currentLC.Airlaunch_Prep.RemoveAll(ap => ap.direction != AirlaunchProject.PrepDirection.Mount && ap.IsComplete());
                    }

                    for (int i = ksc.Constructions.Count; i-- > 0;)
                    {
                        ksc.Constructions[i].IncrementProgress(UTDiff);
                    }

                    // Remove all completed items
                    for (int i = ksc.LaunchComplexes.Count; i-- > 0;)
                    {
                        ksc.LaunchComplexes[i].PadConstructions.RemoveAll(ub => ub.upgradeProcessed);
                    }
                    ksc.LCConstructions.RemoveAll(ub => ub.upgradeProcessed);
                    ksc.FacilityUpgrades.RemoveAll(ub => ub.upgradeProcessed);
                }
                
                double researchTime = UTDiff;
                while (researchTime > 0d && KerbalConstructionTimeData.Instance.TechList.Count > 0)
                {
                    researchTime = KerbalConstructionTimeData.Instance.TechList[0].IncrementProgress(UTDiff);
                }

                if (KerbalConstructionTimeData.Instance.fundTarget.IsValid && KerbalConstructionTimeData.Instance.fundTarget.GetTimeLeft() < 0.5d)
                    KerbalConstructionTimeData.Instance.fundTarget.Clear();
            }
            Profiler.EndSample();
        }

        private void UpdateTechYearMults()
        {
            for (int i = KerbalConstructionTimeData.Instance.TechList.Count - 1; i >= 0; i--)
            {
                var t = KerbalConstructionTimeData.Instance.TechList[i];
                t.UpdateBuildRate(i);
            }
        }

        public void DelayedStart()
        {
            if (KCTUtilities.CurrentGameIsMission()) return;

            RP0Debug.Log("DelayedStart start");
            if (PresetManager.Instance?.ActivePreset == null || !PresetManager.Instance.ActivePreset.GeneralSettings.Enabled)
                return;

            if (KCT_GUI.IsPrimarilyDisabled) return;

            //The following should only be executed when fully enabled for the save

            if (KerbalConstructionTimeData.Instance.ActiveSC == null)
            {
                // This should not be hit, because either KSCSwitcher's LastKSC loads after KCTData
                // or KCTData loads first and the harmony patch runs.
                // But I'm leaving it here just in case.
                KerbalConstructionTimeData.Instance.SetActiveKSCToRSS();
            }

            RP0Debug.Log("Checking vessels for missing parts.");
            //check that all parts are valid in all ships. If not, warn the user and disable that vessel (once that code is written)
            if (!VesselErrorAlerted)
            {
                var erroredVessels = new List<VesselProject>();
                foreach (SpaceCenter KSC in KerbalConstructionTimeData.Instance.KSCs) //this is faster on subsequent scene changes
                {
                    foreach (LaunchComplex currentLC in KSC.LaunchComplexes)
                    {
                        foreach (VesselProject blv in currentLC.BuildList)
                        {
                            if (!blv.AllPartsValid)
                            {
                                RP0Debug.Log(blv.shipName + " contains invalid parts!");
                                erroredVessels.Add(blv);
                            }
                        }
                        foreach (VesselProject blv in currentLC.Warehouse)
                        {
                            if (!blv.AllPartsValid)
                            {
                                RP0Debug.Log(blv.shipName + " contains invalid parts!");
                                erroredVessels.Add(blv);
                            }
                        }
                    }
                }
                if (erroredVessels.Count > 0)
                    PopUpVesselError(erroredVessels);
                VesselErrorAlerted = true;
            }

            if (HighLogic.LoadedSceneIsEditor && EditorShipEditingMode)
            {
                RP0Debug.Log($"Editing {KerbalConstructionTimeData.Instance.EditedVessel.shipName}");
                EditorLogic.fetch.shipNameField.text = KerbalConstructionTimeData.Instance.EditedVessel.shipName;
            }

            if (HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                RP0Debug.Log("SP Start");
                if (!KCT_GUI.IsPrimarilyDisabled)
                {
                    if (ToolbarManager.ToolbarAvailable && Settings.PreferBlizzyToolbar)
                    {
                        if (ShowWindows[0])
                            KCT_GUI.ToggleVisibility(true);
                        else
                        {
                            if (KCTEvents.Instance != null && KerbalConstructionTimeData.ToolbarControl != null)
                            {
                                if (ShowWindows[0])
                                    KCT_GUI.ToggleVisibility(true);
                            }
                        }
                    }
                    KCT_GUI.ResetBLWindow();
                }
                else
                {
                    KCT_GUI.GUIStates.ShowBuildList = false;
                    ShowWindows[0] = false;
                }
                RP0Debug.Log("SP UI done");

                if (KerbalConstructionTimeData.Instance.IsFirstStart)
                {
                    RP0Debug.Log("Showing first start.");
                    KerbalConstructionTimeData.Instance.IsFirstStart = false;
                    KCT_GUI.GUIStates.ShowFirstRun = true;
                    foreach (var ksc in KerbalConstructionTimeData.Instance.KSCs)
                        ksc.EnsureStartingLaunchComplexes();

                    KerbalConstructionTimeData.Instance.Applicants = Database.SettingsSC.GetStartingPersonnel(HighLogic.CurrentGame.Mode);
                }
                else if (KerbalConstructionTimeData.Instance.FirstRunNotComplete)
                {
                    KCT_GUI.GUIStates.ShowFirstRun = true;
                }

                RP0Debug.Log("SP done");
            }

            if (HighLogic.LoadedSceneIsFlight && KerbalConstructionTimeData.Instance.IsSimulatedFlight)
            {
                KCTUtilities.EnableSimulationLocks();
                if (KerbalConstructionTimeData.Instance.SimulationParams.SimulationUT > 0 &&
                    FlightDriver.CanRevertToPrelaunch)    // Used for checking whether the player has saved and then loaded back into that save
                {
                    // Advance building construction
                    double UToffset = KerbalConstructionTimeData.Instance.SimulationParams.SimulationUT - Planetarium.GetUniversalTime();
                    if (UToffset > 0)
                    {
                        foreach (var ksc in KerbalConstructionTimeData.Instance.KSCs)
                        {
                            for(int i = 0; i < ksc.Constructions.Count; ++i)
                            {
                                var c = ksc.Constructions[i];
                                double t = c.GetTimeLeft();
                                if (t <= UToffset)
                                    c.progress = c.BP;
                            }
                        }
                    }
                    RP0Debug.Log($"Setting simulation UT to {KerbalConstructionTimeData.Instance.SimulationParams.SimulationUT}");
                    if (!KCTUtilities.IsPrincipiaInstalled)
                        Planetarium.SetUniversalTime(KerbalConstructionTimeData.Instance.SimulationParams.SimulationUT);
                    else
                        StartCoroutine(EaseSimulationUT_Coroutine(Planetarium.GetUniversalTime(), KerbalConstructionTimeData.Instance.SimulationParams.SimulationUT));
                }

                AddSimulationWatermark();
            }

            if (KerbalConstructionTimeData.Instance.IsSimulatedFlight && HighLogic.LoadedSceneIsGame && !HighLogic.LoadedSceneIsFlight)
            {
                string msg = $"The current save appears to be a simulation and we cannot automatically find a suitable pre-simulation save. Please load an older save manually; we recommend the backup that should have been saved to \\saves\\{HighLogic.SaveFolder}\\Backup\\KCT_simulation_backup.sfs";
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "errorPopup", "Simulation Error", msg, "Understood", false, HighLogic.UISkin);
            }

            RP0Debug.Log("DelayedStart finished");
        }

        private IEnumerator EaseSimulationUT_Coroutine(double startUT, double targetUT)
        {
            const double dayInSeconds = 86_400;

            if (targetUT <= Planetarium.GetUniversalTime()) yield break;

            RP0Debug.Log($"Easing jump to simulation UT in {dayInSeconds}s steps");

            int currentFrame = Time.frameCount;
            double nextUT = startUT;
            while (targetUT - nextUT > dayInSeconds)
            {
                nextUT += dayInSeconds;

                FlightDriver.fetch.framesBeforeInitialSave += Time.frameCount - currentFrame;
                currentFrame = Time.frameCount;
                OrbitPhysicsManager.HoldVesselUnpack();
                Planetarium.SetUniversalTime(nextUT);

                yield return new WaitForFixedUpdate();
            }

            OrbitPhysicsManager.HoldVesselUnpack();
            Planetarium.SetUniversalTime(targetUT);
        }

        public static void PopUpVesselError(List<VesselProject> errored)
        {
            DialogGUIBase[] options = new DialogGUIBase[2];
            options[0] = new DialogGUIButton("Understood", () => { });
            options[1] = new DialogGUIButton("Delete Vessels", () =>
            {
                foreach (VesselProject blv in errored)
                {
                    blv.RemoveFromBuildList(out _);
                    KCTUtilities.AddFunds(blv.GetTotalCost(), TransactionReasonsRP0.VesselPurchase);
                    //remove any associated recon_rollout
                }
            });

            string txt = "The following stored/building vessels contain missing or invalid parts and have been quarantined. Either add the missing parts back into your game or delete the vessels. A file containing the ship names and missing parts has been added to your save folder.\n";
            string txtToWrite = "";
            foreach (VesselProject blv in errored)
            {
                txt += blv.shipName + "\n";
                txtToWrite += blv.shipName + "\n";
                txtToWrite += string.Join("\n", blv.GetMissingParts());
                txtToWrite += "\n\n";
            }

            //make new file for missing ships
            string filename = KSPUtil.ApplicationRootPath + "/saves/" + HighLogic.SaveFolder + "/missingParts.txt";
            File.WriteAllText(filename, txtToWrite);

            //remove all rollout and recon items since they're invalid without the ships
            foreach (VesselProject blv in errored)
            {
                //remove any associated recon_rollout
                foreach (SpaceCenter ksc in KerbalConstructionTimeData.Instance.KSCs)
                {
                    foreach (LaunchComplex currentLC in ksc.LaunchComplexes)
                    {
                        for (int i = 0; i < currentLC.Recon_Rollout.Count; i++)
                        {
                            ReconRolloutProject rr = currentLC.Recon_Rollout[i];
                            if (rr.associatedID == blv.shipID.ToString())
                            {
                                currentLC.Recon_Rollout.Remove(rr);
                                i--;
                            }
                        }

                        for (int i = 0; i < currentLC.Airlaunch_Prep.Count; i++)
                        {
                            AirlaunchProject ap = currentLC.Airlaunch_Prep[i];
                            if (ap.associatedID == blv.shipID.ToString())
                            {
                                currentLC.Airlaunch_Prep.Remove(ap);
                                i--;
                            }
                        }
                    }
                }
            }

            var diag = new MultiOptionDialog("missingPartsPopup", txt, "Vessels Contain Missing Parts", null, options);
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), diag, false, HighLogic.UISkin);
        }

        public static void ShowLaunchAlert(string launchSite)
        {
            RP0Debug.Log("Showing Launch Alert");
            if (KCT_GUI.IsPrimarilyDisabled)
            {
                EditorLogic.fetch.launchVessel();
            }
            else
            {
                KCTUtilities.TryAddVesselToBuildList(launchSite);
                // We are recalculating because vessel validation might have changed state.
                Instance.IsEditorRecalcuationRequired = true;
            }
        }

        // TS code
        private void Fly()
        {
            _flyCallback.Invoke();
        }

        private void PopupNoKCTRecoveryInTS()
        {
            DialogGUIBase[] options = new DialogGUIBase[2];
            options[0] = new DialogGUIButton("Go to Flight scene", Fly);
            options[1] = new DialogGUIButton("Cancel", () => { });

            var diag = new MultiOptionDialog("recoverVesselPopup", "Vessels can only be recovered for reuse in the Flight scene", "Recover Vessel", null, options: options);
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), diag, false, HighLogic.UISkin).HideGUIsWhilePopup();
        }

        private void RecoverToVAB()
        {
            if (!HighLogic.LoadedSceneIsFlight)
            {
                PopupNoKCTRecoveryInTS();
                return;
            }

            if (!KCTUtilities.RecoverActiveVesselToStorage(VesselProject.ListType.VAB))
            {
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "vesselRecoverErrorPopup", "Error!", "There was an error while recovering the ship. Sometimes reloading the scene and trying again works. Sometimes a vessel just can't be recovered this way and you must use the stock recover system.", KSP.Localization.Localizer.GetStringByTag("#autoLOC_190905"), false, HighLogic.UISkin).HideGUIsWhilePopup();
            }
        }

        private void RecoverToSPH()
        {
            if (!HighLogic.LoadedSceneIsFlight)
            {
                PopupNoKCTRecoveryInTS();
                return;
            }

            if (!KCTUtilities.RecoverActiveVesselToStorage(VesselProject.ListType.SPH))
            {
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "recoverShipErrorPopup", "Error!", "There was an error while recovering the ship. Sometimes reloading the scene and trying again works. Sometimes a vessel just can't be recovered this way and you must use the stock recover system.", KSP.Localization.Localizer.GetStringByTag("#autoLOC_190905"), false, HighLogic.UISkin).HideGUIsWhilePopup();
            }
        }

        private void DoNormalRecovery()
        {
            _recoverCallback.Invoke();
        }

        private void RecoveryChoiceTS()
        {
            if (!(_trackingStation != null && _trackingStation.SelectedVessel is Vessel selectedVessel))
            {
                RP0Debug.LogError("No Vessel selected.");
                return;
            }

            bool canRecoverSPH = KCTUtilities.IsSphRecoveryAvailable(selectedVessel);
            bool canRecoverVAB = KCTUtilities.IsVabRecoveryAvailable(selectedVessel);

            var options = new List<DialogGUIBase>();
            if (canRecoverSPH)
                options.Add(new DialogGUIButton("Recover to SPH", RecoverToSPH));
            if (canRecoverVAB)
                options.Add(new DialogGUIButton("Recover to VAB", RecoverToVAB));
            options.Add(new DialogGUIButton("Normal recovery", DoNormalRecovery));
            options.Add(new DialogGUIButton("Cancel", () => { }));

            var diag = new MultiOptionDialog("scrapVesselPopup", string.Empty, "Recover Vessel", null, options: options.ToArray());
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), diag, false, HighLogic.UISkin).HideGUIsWhilePopup();
        }

        private void RecoveryChoiceFlight()
        {
            if (KerbalConstructionTimeData.Instance.IsSimulatedFlight)
            {
                KCT_GUI.GUIStates.ShowSimulationGUI = true;
                return;
            }

            bool isSPHAllowed = KCTUtilities.IsSphRecoveryAvailable(FlightGlobals.ActiveVessel);
            bool isVABAllowed = KCTUtilities.IsVabRecoveryAvailable(FlightGlobals.ActiveVessel);
            var options = new List<DialogGUIBase>();
            if (!FlightGlobals.ActiveVessel.isEVA)
            {
                string nodeTitle = ResearchAndDevelopment.GetTechnologyTitle(Database.SettingsSC.VABRecoveryTech);
                string techLimitText = string.IsNullOrEmpty(nodeTitle) ? string.Empty :
                                       $"\nAdditionally requires {nodeTitle} tech node to be researched (unless the vessel is in Prelaunch state).";
                string genericReuseText = "Allows the vessel to be launched again after a short recovery delay.";

                options.Add(new DialogGUIButtonWithTooltip("Recover to SPH", RecoverToSPH)
                {
                    OptionInteractableCondition = () => isSPHAllowed,
                    tooltipText = isSPHAllowed ? genericReuseText : "Can only be used when the vessel was built in SPH."
                });

                options.Add(new DialogGUIButtonWithTooltip("Recover to VAB", RecoverToVAB)
                {
                    OptionInteractableCondition = () => isVABAllowed,
                    tooltipText = isVABAllowed ? genericReuseText : $"Can only be used when the vessel was built in VAB.{techLimitText}"
                });

                options.Add(new DialogGUIButtonWithTooltip("Normal recovery", DoNormalRecovery)
                {
                    tooltipText = "Vessel will be scrapped and the total value of recovered parts will be refunded."
                });
            }
            else
            {
                options.Add(new DialogGUIButtonWithTooltip("Recover", DoNormalRecovery));
            }

            options.Add(new DialogGUIButton("Cancel", () => { }));

            var diag = new MultiOptionDialog("RecoverVesselPopup",
                string.Empty,
                "Recover vessel",
                null, options: options.ToArray());
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), diag, false, HighLogic.UISkin).HideGUIsWhilePopup();
        }
    }
}

/*
    KerbalConstructionTime (c) by Michael Marvin, Zachary Eck

    KerbalConstructionTime is licensed under a
    Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License.

    You should have received a copy of the license along with this
    work. If not, see <http://creativecommons.org/licenses/by-nc-sa/4.0/>.
*/