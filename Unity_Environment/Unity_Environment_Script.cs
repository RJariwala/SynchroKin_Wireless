// ============================================================
//  SynchroKin.cs  —  Unity IMU Receiver & Avatar Controller
//  Refactored for Dynamic Node Routing (Alpha to Epsilon)
//  BNO055 Absolute Tracking (Constraints/Yaw Stripping Removed)
//  + Dropdown Styling & Full 5-Limb Support
// ============================================================

using UnityEngine;
using UnityEngine.UI;
using System.IO.Ports;
using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using SFB; // Standalone File Browser for saving CSV/JSON files
using UnityEngine.Video;

public class SynchroKin : MonoBehaviour
{
    // ── Enums ──────────────────────────────────────────────────────────────
    // Defines the overarching mode the application is currently running in.
    public enum AppMode  { None, DataExtraction, LiveFreePlay }
    // Defines the specific screen or phase the user is currently looking at.
    public enum AppState { Dashboard, ParameterSelection, CalibrationPhase,
                           PrepPhase, ActionPhase, Report, FreePlay }
    // Used to remap the physical IMU axes (X,Y,Z) to Unity's coordinate system.
    public enum AxisMap  { X, Y, Z, NegX, NegY, NegZ }
    // Represents the 5 physical hardware modules (plus a 'None' option to disable a limb).
    public enum HardwareNode { None, Alpha, Beta, Gamma, Delta, Epsilon } 

    // ── Dynamic Node Assignment ────────────────────────────────────────────
    [Header("Dynamic Node Assignment")]
    // These variables hold which physical node is currently strapped to which body part.
    // They are updated dynamically via the UI dropdowns in the dashboard.
    public HardwareNode chestNode = HardwareNode.Alpha;
    public HardwareNode rightBicepNode = HardwareNode.Beta;
    public HardwareNode leftBicepNode = HardwareNode.Gamma;
    public HardwareNode rightForearmNode = HardwareNode.None;
    public HardwareNode leftForearmNode = HardwareNode.None;

    [Header("Default Pose (used when a slot has no node assigned)")]
    [Tooltip("Local rotation (Euler angles) this bone holds at when its HardwareNode is set to None.")]
    // If a limb doesn't have a sensor assigned (HardwareNode.None), it falls back to these angles
    // so the avatar doesn't collapse or look broken.
    public Vector3 chestDefaultPose = Vector3.zero;
    public Vector3 rightBicepDefaultPose = Vector3.zero;
    public Vector3 leftBicepDefaultPose = Vector3.zero;
    public Vector3 rightForearmDefaultPose = Vector3.zero;
    public Vector3 leftForearmDefaultPose = Vector3.zero;
    
    [Header("Calibration Poses (Your physical posture when pressing Space)")]
    // When the user stands in a T-Pose and hits Spacebar, the system assumes their body matches these angles.
    // It calculates the mathematical offset between their real IMU reading and this perfect pose.
    public Vector3 chestCalibrationPose = Vector3.zero;
    public Vector3 rightBicepCalibrationPose = Vector3.zero;
    public Vector3 leftBicepCalibrationPose = Vector3.zero;
    public Vector3 rightForearmCalibrationPose = Vector3.zero;
    public Vector3 leftForearmCalibrationPose = Vector3.zero;
    
    // ── Node Dropdown References (for styling) ─────────────────────────────
    [Header("Node Dropdown References (for styling)")]
    // UI elements that let the user assign physical nodes to digital limbs.
    public TMP_Dropdown dropdownChest;
    public TMP_Dropdown dropdownRightBicep;
    public TMP_Dropdown dropdownLeftBicep;
    public TMP_Dropdown dropdownRightForearm;
    public TMP_Dropdown dropdownLeftForearm;

    [Header("Dropdown Styling")]
    // Variables used by the custom inspector script at the bottom to force a unified style
    // across all dropdowns (font size, box size, colors) without manually clicking through Unity's UI hierarchy.
    public int   dropdownItemFontSize = 20;
    public float dropdownItemHeight   = 32f;
    public int   dropdownCaptionFontSize = 18;
    public float dropdownTemplateWidth = 250f;
    public float dropdownTemplateHeight = 200f;
    public Color dropdownFontColor = Color.white;
    public Color dropdownBackgroundColor = new Color(0.1f, 0.1f, 0.12f, 1f);

    // ── State machine ──────────────────────────────────────────────────────
    [Header("UI State Machine")]
    // Tracks exactly where we are in the app flow to enable/disable UI panels.
    public AppMode  currentMode  = AppMode.None;
    public AppState currentState = AppState.Dashboard;

    // ── Canvas panels ─────────────────────────────────────────────────────
    [Header("Canvas Panels & Startup")]
    // References to the large UI groups so we can toggle them on/off easily.
    public GameObject panelDashboard, panelSelection, panelActiveTest,
                      panelReport, backButton, startupCurtain;
    public VideoPlayer logoVideo; // Used to play the intro splash screen.

    // ── Data-extraction buttons ────────────────────────────────────────────
    [Header("Data Extraction UI")]
    // Buttons available at the end of a clinical test to save the results.
    public GameObject btnExportCSV, btnExportJSON, btnExportGraph, btnEndTest;
    
    // Tracks the exact system time a test started, used to timestamp the exported data.
    private DateTime _testStartTime;

    // ── Text elements ──────────────────────────────────────────────────────
    [Header("UI Text Elements")]
    // Text mesh pro components for displaying live feedback to the user and doctor.
    public TextMeshProUGUI textInstructions, textLiveScore,
                           textLiveMetrics, textFinalFeedback;

    // ── Hardware Diagnostics UI ────────────────────────────────────────────
    [Header("Hardware Diagnostics UI")]
    // Little colored dots on the UI that turn blue when a node is connected, and grey when dead.
    public Image chestNodeIndicator;
    public Image bicepNodeIndicator; // Right Bicep
    public Image leftBicepNodeIndicator;   
    public Image rightForearmNodeIndicator;
    public Image leftForearmNodeIndicator;
    
    public Color nodeConnectedColor = new Color(0f, 0.9f, 0.87f, 1f); // Cyan/Blue
    public Color nodeDisconnectedColor = new Color(0.3f, 0.3f, 0.3f, 0.6f); // Faded Grey
    
    // Tracks the exact Time.time we last heard from a specific hardware node.
    // If this gets too old (e.g., > 1.5 seconds), we consider the node disconnected.
    private float _lastAlphaPing = -999f;
    private float _lastBetaPing = -999f;
    private float _lastGammaPing = -999f;
    private float _lastDeltaPing = -999f;
    private float _lastEpsilonPing = -999f;
    
    // Tracks the current visual state of the UI dots so we don't update them unnecessarily every frame.
    private bool _isChestUIOn = false;
    private bool _isBicepUIOn = false;
    private bool _isLeftBicepUIOn = false;   
    private bool _isRightForearmUIOn = false;
    private bool _isLeftForearmUIOn = false;
    // Forces a refresh of the UI colors, usually called when entering the dashboard.
    private bool _forceUIUpdate = true;

    // ── Live graph ────────────────────────────────────────────────────────
    [Header("Live Graphing (Native)")]
    // Uses a raw texture to draw pixels directly onto the screen for the live sway graph.
    public RawImage graphDisplay;
    private Texture2D _graphTexture;
    private int graphWidth = 600, graphHeight = 400; // Resolution of the graph texture.
    public float maxGraphAngle = 10f; // The Y-axis scale (e.g., +/- 10 degrees of sway).

    // ── Camera ────────────────────────────────────────────────────────────
    [Header("Camera Control")]
    // Smoothly moves the camera between the main menu view and the active tracking view.
    public Transform mainCamera, viewStandard, viewRecording;
    public float cameraSpeed = 4f;

    // ── Patient scaling ───────────────────────────────────────────────────
    [Header("Patient Scaling")]
    public Transform  liveAvatarRoot; // The parent object of the 3D model.
    public TMP_InputField heightInputField; // Where the doctor types the patient's height in cm.
    public float      defaultModelHeightCm = 175f; // The base height of the 3D model as imported.

    // ── Environment ───────────────────────────────────────────────────────
    [Header("Environment")]
    public GameObject      liveAvatarModel, liveMat; // Visual representations of the patient.
    public SkinnedMeshRenderer avatarSkin; // Used to change the color of the avatar (e.g., orange during calibration).

    // ── Serial ────────────────────────────────────────────────────────────
    [Header("Hardware Settings")]
    public TMP_InputField portInputField; // UI input for COM port.
    public string portName          = "COM13"; // Default COM port.
    public int    baudRate          = 115200; // MUST MATCH THE ESP32 MASTER BAUD RATE!
    public bool   useDTR            = false; // Data Terminal Ready (usually false for ESP32).
    public float  autoReconnectDelay = 3.0f; // Time to wait before attempting to reopen a crashed serial port.
    
    private SerialPort    _serial;
    // A highly optimized string builder to catch incoming serial data without causing garbage collection stutter.
    private readonly System.Text.StringBuilder _rxBuffer = new System.Text.StringBuilder(4096);
    private float         _lastDataReceivedTime = 0f;
    private bool          _serialOpen          = false;

    // Pre-allocated arrays for string splitting (prevents memory allocation overhead every frame).
    private static readonly char[] LineSplit  = { '\n' };
    private static readonly char[] SpaceSplit = { ' ' };

    // ── Sensor Axis Mapping (Live Edit) ────────────────────────────────────
    [Header("Sensor Axis Mapping (Live Edit)")]
    // Since strapping an IMU to a body part might flip its orientation (e.g., upside down),
    // these dropdowns allow us to remap the hardware X/Y/Z to the Unity Bone X/Y/Z on the fly.
    public AxisMap chestMapX = AxisMap.NegX;
    public AxisMap chestMapY = AxisMap.NegY;
    public AxisMap chestMapZ = AxisMap.Z;

    public AxisMap bicepMapX = AxisMap.Y;
    public AxisMap bicepMapY = AxisMap.X;
    public AxisMap bicepMapZ = AxisMap.Z;
    
    public AxisMap leftBicepMapX = AxisMap.Y;
    public AxisMap leftBicepMapY = AxisMap.X;
    public AxisMap leftBicepMapZ = AxisMap.Z;

    public AxisMap rightForearmMapX = AxisMap.Y;
    public AxisMap rightForearmMapY = AxisMap.X;
    public AxisMap rightForearmMapZ = AxisMap.Z;

    public AxisMap leftForearmMapX = AxisMap.Y;
    public AxisMap leftForearmMapY = AxisMap.X;
    public AxisMap leftForearmMapZ = AxisMap.Z;

    // ── Smoothing & Filters ───────────────────────────────────────────────
    [Header("Kinematic Smoothing & Filters")]
    // A glitch filter: If an IMU spits out a bad quaternion and the bone tries to snap 80 degrees in one frame, we ignore it.
    public float maxAllowedJumpPerFrame = 80f; 
    [Tooltip("Visual smoothing speed. 8–15 = responsive, 4–6 = buttery.")]
    [Range(2f, 30f)]
    // This is the 't' value for the Quaternion.Slerp. Higher = snappy/jittery. Lower = smooth but lagged.
    public float liveSmoothSpeed = 8f;

    // Tracks how many consecutive frames a bone has tried to "glitch". If it stays glitched for 5 frames,
    // we assume it's an actual fast movement and let it snap to the new position.
    private int _chestGlitchFrames = 0;
    private int _bicepGlitchFrames = 0;
    private int _leftBicepGlitchFrames = 0;   
    private int _rightForearmGlitchFrames = 0;
    private int _leftForearmGlitchFrames = 0;

    // ── Posture Offsets ───────────────────────────────────────────────────
    [Header("Calibration Offsets")]
    public bool   calibrateArmDown = true; // Determines if T-pose or arms-down pose is expected during calibration.
    
    // ── Skeletal targets ──────────────────────────────────────────────────
    [Header("Skeletal Targets")]
    // References to the actual Transforms (bones) inside the 3D humanoid rig.
    public Transform liveChestBone, liveBicepBone; // liveBicepBone = Right Arm
    public Transform liveLeftBicepBone; 
    public Transform liveRightForearmBone;
    public Transform liveLeftForearmBone;

    // ── Calibration references ────────────────────────────────────────────
    // _CalibRaw stores the exact raw hardware quaternion emitted by the IMU the moment the user pressed Spacebar.
    private Quaternion _chestCalibRaw   = Quaternion.identity;
    private Quaternion _bicepCalibRaw   = Quaternion.identity;
    private Quaternion _leftBicepCalibRaw = Quaternion.identity;  
    private Quaternion _rightForearmCalibRaw = Quaternion.identity;
    private Quaternion _leftForearmCalibRaw = Quaternion.identity;

    // _target stores the calculated final orientation (after math and offsets) that the Unity bone SHOULD move towards.
    private Quaternion _targetChest   = Quaternion.identity;
    private Quaternion _targetBicep   = Quaternion.identity;
    private Quaternion _targetLeftBicep = Quaternion.identity;   
    private Quaternion _targetRightForearm = Quaternion.identity;
    private Quaternion _targetLeftForearm = Quaternion.identity;

    // _lastIn stores the quaternion from the PREVIOUS frame to enforce mathematical continuity (preventing quaternion flip-flop issues).
    private Quaternion _lastChestIn   = Quaternion.identity;
    private Quaternion _lastBicepIn   = Quaternion.identity;
    private Quaternion _lastLeftBicepIn = Quaternion.identity;   
    private Quaternion _lastRightForearmIn = Quaternion.identity;
    private Quaternion _lastLeftForearmIn = Quaternion.identity;

    // Flag triggered when the user presses Spacebar.
    private bool _needsCalibration = false;

    // ── Test / report state ───────────────────────────────────────────────
    private string _activeTestName = "";
    // Timers used to countdown phases (e.g., "Prep phase: 3... 2... 1...")
    private float  _phaseTimer, _testDuration = 15f, _uiRefreshTimer;

    // Lists holding data arrays for exporting to CSV/JSON later.
    private List<PatientDataPoint> _patientDataLog  = new List<PatientDataPoint>();
    private List<float> _pitchSwayData   = new List<float>(); // Anterior-Posterior Sway
    private List<float> _rollSwayData    = new List<float>(); // Medial-Lateral Sway
    private List<float> _combinedSwayData= new List<float>(); // Root Mean Square of Pitch & Roll

    // Final calculated metrics for the clinical report card.
    private float _finalApRMS, _finalApPeak, _finalMlRMS,
                  _finalMlPeak, _finalCombinedRMS;

    // A structure defining exactly what data is captured every single frame during a test.
    public class PatientDataPoint
    {
        public string   stepName; // e.g. "Postural Sway Test"
        public float    timeStamp; // Time elapsed since test start
        public string   sysTimeStr; // Absolute computer time (HH:mm:ss:fff)
        public Quaternion chestRot, bicepRot, leftBicepRot, rightForearmRot, leftForearmRot;
    }

    // ══════════════════════════════════════════════════════════════════════
    void Start()
    {
        // 1. Pull previous calibration offsets from PlayerPrefs (so you don't have to recalibrate every app launch).
        LoadCalibration();
        
        // 2. Setup the UI port text box based on the default value.
        if (portInputField != null) portInputField.text = portName;

        // 3. Initialize a blank black texture for the real-time graphing feature.
        _graphTexture = new Texture2D(graphWidth, graphHeight, TextureFormat.RGBA32, false);
        if (graphDisplay != null) graphDisplay.texture = _graphTexture;

        // 4. Attempt to connect to the ESP32 Master Node via USB.
        OpenConnection();
        
        // 5. Kick off the logo splash screen routine.
        StartCoroutine(StartupSequence());

        // 6. Bind the dropdown menus so that when a user selects "Alpha", it calls the SetChestNode function dynamically.
        WireDropdown(dropdownChest, SetChestNode);
        WireDropdown(dropdownRightBicep, SetRightBicepNode);
        WireDropdown(dropdownLeftBicep, SetLeftBicepNode);
        WireDropdown(dropdownRightForearm, SetRightForearmNode);
        WireDropdown(dropdownLeftForearm, SetLeftForearmNode);

        // 7. Fade out the splash screen after a short delay.
        if (startupCurtain != null)
        {
            StartCoroutine(RemoveCurtainRoutine());
        }
    }

    private System.Collections.IEnumerator RemoveCurtainRoutine()
    {
        yield return new WaitForSeconds(2f); // Wait 2 seconds
        if (startupCurtain != null) startupCurtain.SetActive(false); // Hide the curtain
    }

    private System.Collections.IEnumerator StartupSequence()
    {
        // Hide all major UI panels during the splash screen.
        if (panelDashboard != null) panelDashboard.SetActive(false);
        if (panelSelection != null) panelSelection.SetActive(false);
        if (panelActiveTest != null) panelActiveTest.SetActive(false);
        if (panelReport != null) panelReport.SetActive(false);
        
        yield return new WaitForSeconds(3f); // Give the video player time to finish.
        GoToDashboard(); // Proceed to the main menu.
    }

    // Helper function to safely bind UI Dropdown events without causing duplicate triggers.
    private void WireDropdown(TMP_Dropdown dd, UnityEngine.Events.UnityAction<int> callback)
    {
        if (dd == null) return;
        dd.onValueChanged.RemoveListener(callback); // Remove old listeners just in case
        dd.onValueChanged.AddListener(callback); // Add the new listener
    }

    void Update()
    {
        // Compile the last known ping times of all 5 possible hardware nodes.
        // Index mapping: 0=None, 1=Alpha, 2=Beta, 3=Gamma, 4=Delta, 5=Epsilon
        float[] pings = { -999f, _lastAlphaPing, _lastBetaPing, _lastGammaPing, _lastDeltaPing, _lastEpsilonPing };

        // Determine if a node is "alive" by checking if:
        // 1. It is actually assigned to a body part (not None)
        // 2. The time since we last received a packet from it is less than 1.5 seconds.
        bool chestAlive   = chestNode != HardwareNode.None && (Time.time - pings[(int)chestNode]) < 1.5f;
        bool bicepAlive   = rightBicepNode != HardwareNode.None && (Time.time - pings[(int)rightBicepNode]) < 1.5f;
        bool lBicepAlive  = leftBicepNode != HardwareNode.None && (Time.time - pings[(int)leftBicepNode]) < 1.5f;
        bool rForearmAlive= rightForearmNode != HardwareNode.None && (Time.time - pings[(int)rightForearmNode]) < 1.5f;
        bool lForearmAlive= leftForearmNode != HardwareNode.None && (Time.time - pings[(int)leftForearmNode]) < 1.5f;

        // Update the tiny UI dots to reflect connection status (Blue = Alive, Grey = Dead/Unassigned).
        UpdateNodeIndicator(chestNodeIndicator,        chestAlive,    ref _isChestUIOn);
        UpdateNodeIndicator(bicepNodeIndicator,        bicepAlive,    ref _isBicepUIOn);
        UpdateNodeIndicator(leftBicepNodeIndicator,    lBicepAlive,   ref _isLeftBicepUIOn);
        UpdateNodeIndicator(rightForearmNodeIndicator, rForearmAlive, ref _isRightForearmUIOn);
        UpdateNodeIndicator(leftForearmNodeIndicator,  lForearmAlive, ref _isLeftForearmUIOn);

        // Turn off the force refresh flag once we've executed it.
        _forceUIUpdate = false;
    }

    // Handles the actual recoloring of the UI Image component.
    private void UpdateNodeIndicator(Image indicator, bool alive, ref bool uiState)
    {
        if (indicator == null) return;
        // Optimization: Only update the UI if the state actually changed (or if a refresh was forced).
        if (alive == uiState && !_forceUIUpdate) return;

        uiState = alive;
        indicator.color = alive ? nodeConnectedColor : nodeDisconnectedColor;
        // Weird Unity quirk: Sometimes UI Images don't immediately refresh their color, so we toggle enabled off/on to force a draw.
        indicator.enabled = false;
        indicator.enabled = true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // LateUpdate runs after Update. It is best practice to move the camera and bones here 
    // to ensure all input and logic has been processed first, preventing visual stuttering.
    void LateUpdate()
    {
        // 1. WATCHDOG: If serial disconnected and 3 seconds passed, try to reconnect automatically.
        if (!_serialOpen && Time.time - _lastDataReceivedTime > autoReconnectDelay)
        {
            OpenConnection();
            _lastDataReceivedTime = Time.time;
        }

        // 2. CALIBRATION INPUT: Listen for the spacebar.
        if (UnityEngine.InputSystem.Keyboard.current.spaceKey.wasPressedThisFrame)
            _needsCalibration = true;

        // 3. READ SERIAL DATA: Parse incoming IMU values and calculate target bone rotations.
        HandleHardwareInput();
        
        // 4. MOVE CAMERA: Slide the camera to the appropriate view based on app state.
        HandleCameraMovement();

        // 5. SET FINAL TARGETS: Local copies of the target rotations.
        Quaternion finalLiveChest = _targetChest;
        Quaternion finalLiveBicep = _targetBicep;
        Quaternion finalLiveLeftBicep = _targetLeftBicep;
        Quaternion finalLiveRightForearm = _targetRightForearm;
        Quaternion finalLiveLeftForearm = _targetLeftForearm;

        // 6. CALIBRATION LOCK: If we are in the calibration countdown, lock the avatar into the rigid T-Pose
        // so the patient knows exactly what posture they need to mimic physically.
        if (currentState == AppState.CalibrationPhase)
        {
            // If a node is assigned, lock to calibration pose. If no node assigned, lock to default resting pose.
            finalLiveChest        = (chestNode != HardwareNode.None)        ? Quaternion.Euler(chestCalibrationPose)    : Quaternion.Euler(chestDefaultPose);
            finalLiveBicep        = (rightBicepNode != HardwareNode.None)   ? Quaternion.Euler(rightBicepCalibrationPose)   : Quaternion.Euler(rightBicepDefaultPose);
            finalLiveLeftBicep    = (leftBicepNode != HardwareNode.None)    ? Quaternion.Euler(leftBicepCalibrationPose)    : Quaternion.Euler(leftBicepDefaultPose);
            finalLiveRightForearm = (rightForearmNode != HardwareNode.None) ? Quaternion.Euler(rightForearmCalibrationPose) : Quaternion.Euler(rightForearmDefaultPose);
            finalLiveLeftForearm  = (leftForearmNode != HardwareNode.None)  ? Quaternion.Euler(leftForearmCalibrationPose)  : Quaternion.Euler(leftForearmDefaultPose);
        }

        // 7. APPLY ROTATIONS WITH SLERP: Smoothly interpolate the bones from current angle to final angle.
        // Slerp (Spherical Linear Interpolation) makes the movement look fluid and human-like instead of robotic.
        float slerpT = Time.deltaTime * liveSmoothSpeed;
        
        // Pure Slerp matches BNO055 Absolute Output directly (No Yaw Stripping required because absolute orientation rules the world).
        if (liveChestBone != null) 
            liveChestBone.localRotation = Quaternion.Slerp(liveChestBone.localRotation, finalLiveChest, slerpT);

        if (liveBicepBone != null) 
            liveBicepBone.localRotation = Quaternion.Slerp(liveBicepBone.localRotation, finalLiveBicep, slerpT);
            
        if (liveLeftBicepBone != null) 
            liveLeftBicepBone.localRotation = Quaternion.Slerp(liveLeftBicepBone.localRotation, finalLiveLeftBicep, slerpT);

        if (liveRightForearmBone != null)
            liveRightForearmBone.localRotation = Quaternion.Slerp(liveRightForearmBone.localRotation, finalLiveRightForearm, slerpT);

        if (liveLeftForearmBone != null)
            liveLeftForearmBone.localRotation = Quaternion.Slerp(liveLeftForearmBone.localRotation, finalLiveLeftForearm, slerpT);

        // 8. UPDATE TIMERS: Progress the countdowns if we are in an active state.
        if (currentState != AppState.Dashboard &&
            currentState != AppState.ParameterSelection &&
            currentState != AppState.Report)
            UpdateStateTimerAndLogic();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Handles opening the USB COM port to talk to the ESP32 Master node.
    void OpenConnection()
    {
        try
        {
            // Clean up any existing broken connection first.
            if (_serial != null && _serial.IsOpen) { _serial.Close(); }
            
            _serial = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 15, // Crash prevention: Don't hang the thread if data stops arriving.
                DtrEnable   = useDTR,
                RtsEnable   = useDTR
            };
            _serial.Open();
            _serial.DiscardInBuffer(); // Flush old garbage data from the pipe.
            _lastDataReceivedTime = Time.time;
            _rxBuffer.Clear();
            _serialOpen = true;
        }
        catch (Exception)
        {
            // If the port doesn't exist or is locked by another program, fail gracefully.
            _serialOpen = false;
        }
    }

    // The core logic for reading the 921600 baud serial stream and turning it into 3D movement.
    private void HandleHardwareInput()
    {
        if (_serial == null || !_serial.IsOpen) { _serialOpen = false; return; }

        try
        {
            // If there's nothing to read, exit early.
            if (_serial.BytesToRead <= 0) return;

            // Dump everything from the USB buffer into our optimized StringBuilder.
            _rxBuffer.Append(_serial.ReadExisting());
            
            // Failsafe: If the buffer is growing infinitely (due to bad formatting), clear it to prevent memory leaks.
            if (_rxBuffer.Length > 4096) { _rxBuffer.Clear(); _serial.DiscardInBuffer(); return; }

            string bufferSnapshot = _rxBuffer.ToString();
            // Find the last complete line (ending in a newline character).
            int lastNewline = bufferSnapshot.LastIndexOf('\n');
            if (lastNewline < 0) return; // Wait for a complete line to arrive.

            // Extract the completed data string up to the newline.
            string completed = bufferSnapshot.Substring(0, lastNewline);
            _rxBuffer.Clear();
            // Put the incomplete fragment (after the newline) back into the buffer for next frame.
            _rxBuffer.Append(bufferSnapshot, lastNewline + 1, bufferSnapshot.Length - (lastNewline + 1));

            // Split the completed data into individual lines.
            string[] lines = completed.Split(LineSplit, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            // Search BACKWARDS to find the absolute freshest line of data (we only care about the present moment).
            string latest = "";
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                // We identify a valid line by ensuring it contains the STAT header and at least the Forearm key.
                if (lines[i].Contains("STAT:") && lines[i].Contains("F_W:"))
                {
                    latest = lines[i].Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(latest)) return;

            // --- STATEFUL WATCHDOG (Parsing the bitmask from the Master ESP32) ---
            // Example format: "STAT:1,1,0,0,0 C_W:..."
            int statStart = latest.IndexOf("STAT:") + 5;
            int spaceAfter = latest.IndexOf(' ', statStart);
            if (spaceAfter == -1) spaceAfter = latest.Length;

            string statBlock = latest.Substring(statStart, spaceAfter - statStart);
            string[] s = statBlock.Split(',');

            // If the bitmask contains a '1' at that index, update the ping timer for that specific hardware node.
            if (s.Length >= 1) { if (s[0].Contains("1")) _lastAlphaPing = Time.time; }
            if (s.Length >= 2) { if (s[1].Contains("1")) _lastBetaPing = Time.time; }
            if (s.Length >= 3) { if (s[2].Contains("1")) _lastGammaPing = Time.time; }
            if (s.Length >= 4) { if (s[3].Contains("1")) _lastDeltaPing = Time.time; }
            if (s.Length >= 5) { if (s[4].Contains("1")) _lastEpsilonPing = Time.time; }

            // Trim out the STAT block so we are left with just the quaternion data string.
            latest = latest.Substring(spaceAfter).Trim();

            // --- DYNAMIC KINEMATIC MATH (Supports up to 5 Nodes safely) ---
            // Split the remaining string by spaces into an array of "Key:Value" strings (e.g. "C_W:0.707").
            string[] parts = latest.Split(SpaceSplit, StringSplitOptions.RemoveEmptyEntries);
            
            // Array Index: 0=None, 1=Alpha, 2=Beta, 3=Gamma, 4=Delta, 5=Epsilon
            // Initialize arrays to hold the parsed Quaternion values (w, x, y, z). Default to an identity quaternion.
            float[] hwW = { 1f, 1f, 1f, 1f, 1f, 1f };
            float[] hwX = { 0f, 0f, 0f, 0f, 0f, 0f };
            float[] hwY = { 0f, 0f, 0f, 0f, 0f, 0f };
            float[] hwZ = { 0f, 0f, 0f, 0f, 0f, 0f };

            // Dynamically parse the array chunks into the correct hardware index based on string length.
            if (parts.Length >= 4)  { TryParseToken(parts[0], out hwW[1]); TryParseToken(parts[1], out hwX[1]); TryParseToken(parts[2], out hwY[1]); TryParseToken(parts[3], out hwZ[1]); }
            if (parts.Length >= 8)  { TryParseToken(parts[4], out hwW[2]); TryParseToken(parts[5], out hwX[2]); TryParseToken(parts[6], out hwY[2]); TryParseToken(parts[7], out hwZ[2]); }
            if (parts.Length >= 12) { TryParseToken(parts[8], out hwW[3]); TryParseToken(parts[9], out hwX[3]); TryParseToken(parts[10], out hwY[3]); TryParseToken(parts[11], out hwZ[3]); }
            if (parts.Length >= 16) { TryParseToken(parts[12], out hwW[4]); TryParseToken(parts[13], out hwX[4]); TryParseToken(parts[14], out hwY[4]); TryParseToken(parts[15], out hwZ[4]); }
            if (parts.Length >= 20) { TryParseToken(parts[16], out hwW[5]); TryParseToken(parts[17], out hwX[5]); TryParseToken(parts[18], out hwY[5]); TryParseToken(parts[19], out hwZ[5]); }

            _lastDataReceivedTime = Time.time;
            _serialOpen           = true;

            // --- AXIS REMAPPING ---
            // We take the raw x, y, and z values from the hardware array and pass them through our Dropdown remappers.
            // This ensures that if the physical sensor was strapped on upside down, Unity can correct it before processing.
            int cI = (int)chestNode;
            Quaternion rawChest = new Quaternion(
                GetMappedAxis(chestMapX, hwX[cI], hwY[cI], hwZ[cI]),
                GetMappedAxis(chestMapY, hwX[cI], hwY[cI], hwZ[cI]),
                GetMappedAxis(chestMapZ, hwX[cI], hwY[cI], hwZ[cI]), hwW[cI]).normalized;

            int rbI = (int)rightBicepNode;
            Quaternion rawBicep = new Quaternion(
                GetMappedAxis(bicepMapX, hwX[rbI], hwY[rbI], hwZ[rbI]),
                GetMappedAxis(bicepMapY, hwX[rbI], hwY[rbI], hwZ[rbI]),
                GetMappedAxis(bicepMapZ, hwX[rbI], hwY[rbI], hwZ[rbI]), hwW[rbI]).normalized;

            int lbI = (int)leftBicepNode;
            Quaternion rawLeftBicep = new Quaternion(
                GetMappedAxis(leftBicepMapX, hwX[lbI], hwY[lbI], hwZ[lbI]),
                GetMappedAxis(leftBicepMapY, hwX[lbI], hwY[lbI], hwZ[lbI]),
                GetMappedAxis(leftBicepMapZ, hwX[lbI], hwY[lbI], hwZ[lbI]), hwW[lbI]).normalized;

            int rfI = (int)rightForearmNode;
            Quaternion rawRightForearm = new Quaternion(
                GetMappedAxis(rightForearmMapX, hwX[rfI], hwY[rfI], hwZ[rfI]),
                GetMappedAxis(rightForearmMapY, hwX[rfI], hwY[rfI], hwZ[rfI]),
                GetMappedAxis(rightForearmMapZ, hwX[rfI], hwY[rfI], hwZ[rfI]), hwW[rfI]).normalized;

            int lfI = (int)leftForearmNode;
            Quaternion rawLeftForearm = new Quaternion(
                GetMappedAxis(leftForearmMapX, hwX[lfI], hwY[lfI], hwZ[lfI]),
                GetMappedAxis(leftForearmMapY, hwX[lfI], hwY[lfI], hwZ[lfI]),
                GetMappedAxis(leftForearmMapZ, hwX[lfI], hwY[lfI], hwZ[lfI]), hwW[lfI]).normalized;

            // --- CONTINUITY ENFORCEMENT ---
            // Quaternions have a mathematical quirk where q and -q represent the exact same 3D rotation,
            // but interpolating between them causes the model to violently spin 360 degrees.
            // This function ensures the quaternion stays on the shortest mathematical hemisphere path.
            rawChest        = EnforceContinuity(_lastChestIn,        rawChest);        _lastChestIn        = rawChest;
            rawBicep        = EnforceContinuity(_lastBicepIn,        rawBicep);        _lastBicepIn        = rawBicep;
            rawLeftBicep    = EnforceContinuity(_lastLeftBicepIn,    rawLeftBicep);    _lastLeftBicepIn    = rawLeftBicep;
            rawRightForearm = EnforceContinuity(_lastRightForearmIn, rawRightForearm); _lastRightForearmIn = rawRightForearm;
            rawLeftForearm  = EnforceContinuity(_lastLeftForearmIn,  rawLeftForearm);  _lastLeftForearmIn  = rawLeftForearm;

            // --- CALIBRATION SNAPSHOT ---
            // If the user pressed spacebar, we save the current raw readings as the new "zero" baseline.
            if (_needsCalibration)
            {
                _chestCalibRaw        = rawChest;
                _bicepCalibRaw        = rawBicep;
                _leftBicepCalibRaw    = rawLeftBicep;
                _rightForearmCalibRaw = rawRightForearm;
                _leftForearmCalibRaw  = rawLeftForearm;

                _needsCalibration = false;
                SaveCalibration(); // Persist it to disk.
            }

            // Convert the Inspector Vector3 offset angles into Quaternions.
            Quaternion chestOffset        = Quaternion.Euler(chestCalibrationPose);
            Quaternion bicepOffset        = Quaternion.Euler(rightBicepCalibrationPose);
            Quaternion leftBicepOffset    = Quaternion.Euler(leftBicepCalibrationPose);
            Quaternion rightForearmOffset = Quaternion.Euler(rightForearmCalibrationPose);
            Quaternion leftForearmOffset  = Quaternion.Euler(leftForearmCalibrationPose);

            // --- DELTA CALCULATION ---
            // Calculate pure "how much has each sensor rotated since calibration".
            // Done by multiplying current reading by the inverse of the reading taken at calibration.
            Quaternion deltaChest        = rawChest        * Quaternion.Inverse(_chestCalibRaw);
            Quaternion deltaBicep        = rawBicep        * Quaternion.Inverse(_bicepCalibRaw);
            Quaternion deltaLeftBicep    = rawLeftBicep    * Quaternion.Inverse(_leftBicepCalibRaw);
            Quaternion deltaRightForearm = rawRightForearm * Quaternion.Inverse(_rightForearmCalibRaw);
            Quaternion deltaLeftForearm  = rawLeftForearm  * Quaternion.Inverse(_leftForearmCalibRaw);

            // --- HIERARCHICAL KINEMATIC MATH (THE TWIST LEAKAGE FIX) ---
            // 1. Calculate the true GLOBAL WORLD orientation for every limb by applying the delta to the offset pose.
            // This represents exactly where the bone is pointing in 3D space, irrespective of the skeleton.
            Quaternion worldChest        = deltaChest * chestOffset;
            Quaternion worldBicep        = deltaBicep * bicepOffset;
            Quaternion worldLeftBicep    = deltaLeftBicep * leftBicepOffset;
            Quaternion worldRightForearm = deltaRightForearm * rightForearmOffset;
            Quaternion worldLeftForearm  = deltaLeftForearm * leftForearmOffset;

            // 2. Extract the strict LOCAL rotations required by Unity's nested bone hierarchy.
            // Because Unity bones inherit rotation from their parents (e.g., rotating the chest moves the arm),
            // we have to mathematically strip out the parent's world rotation to find the true local joint angle.
            // Formula: Local = Inverse(ParentWorld) * ChildWorld
            Quaternion absChest        = worldChest; // Chest has no IMU parent, so its world = local.
            Quaternion absBicep        = Quaternion.Inverse(worldChest) * worldBicep; // Strip Chest from Bicep
            Quaternion absLeftBicep    = Quaternion.Inverse(worldChest) * worldLeftBicep; // Strip Chest from Left Bicep
            Quaternion absRightForearm = Quaternion.Inverse(worldBicep) * worldRightForearm; // Strip Bicep from Forearm
            Quaternion absLeftForearm  = Quaternion.Inverse(worldLeftBicep) * worldLeftForearm; // Strip L Bicep from L Forearm           

            // ── BONE ASSIGNMENTS & GLITCH FILTER ──
            // Before applying the final math to the actual target variable, we run it through the glitch filter.
            // If the angle jumps too sharply (> maxAllowedJumpPerFrame), we freeze it for a few frames.
            if (chestNode == HardwareNode.None) { _targetChest = Quaternion.Euler(chestDefaultPose); _chestGlitchFrames = 0; }
            else 
            { 
                float chestJump = Quaternion.Angle(_targetChest, absChest);
                if (chestJump > maxAllowedJumpPerFrame && _chestGlitchFrames < 5) { _chestGlitchFrames++; }
                else { _targetChest = absChest; _chestGlitchFrames = 0; }
            }

            if (rightBicepNode == HardwareNode.None) { _targetBicep = Quaternion.Euler(rightBicepDefaultPose); _bicepGlitchFrames = 0; }
            else 
            { 
                float bicepJump = Quaternion.Angle(_targetBicep, absBicep);
                if (bicepJump > maxAllowedJumpPerFrame && _bicepGlitchFrames < 5) { _bicepGlitchFrames++; }
                else { _targetBicep = absBicep; _bicepGlitchFrames = 0; }
            }

            if (leftBicepNode == HardwareNode.None) { _targetLeftBicep = Quaternion.Euler(leftBicepDefaultPose); _leftBicepGlitchFrames = 0; }
            else 
            { 
                float leftBicepJump = Quaternion.Angle(_targetLeftBicep, absLeftBicep);
                if (leftBicepJump > maxAllowedJumpPerFrame && _leftBicepGlitchFrames < 5) { _leftBicepGlitchFrames++; }
                else { _targetLeftBicep = absLeftBicep; _leftBicepGlitchFrames = 0; }
            }

            if (rightForearmNode == HardwareNode.None) { _targetRightForearm = Quaternion.Euler(rightForearmDefaultPose); _rightForearmGlitchFrames = 0; }
            else 
            { 
                float jump = Quaternion.Angle(_targetRightForearm, absRightForearm);
                if (jump > maxAllowedJumpPerFrame && _rightForearmGlitchFrames < 5) { _rightForearmGlitchFrames++; }
                else { _targetRightForearm = absRightForearm; _rightForearmGlitchFrames = 0; }
            }

            if (leftForearmNode == HardwareNode.None) { _targetLeftForearm = Quaternion.Euler(leftForearmDefaultPose); _leftForearmGlitchFrames = 0; }
            else 
            { 
                float jump = Quaternion.Angle(_targetLeftForearm, absLeftForearm);
                if (jump > maxAllowedJumpPerFrame && _leftForearmGlitchFrames < 5) { _leftForearmGlitchFrames++; }
                else { _targetLeftForearm = absLeftForearm; _leftForearmGlitchFrames = 0; }
            }
        }
        catch (Exception e)
        {
#if UNITY_EDITOR
            // Catch malformed strings quietly so they don't crash the program.
            Debug.LogWarning($"[SynchroKin] Packet handling error: {e.Message}");
#endif
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Controls the logic for counting down timers during clinical testing phases.
    private void UpdateStateTimerAndLogic()
    {
        // LIVE FREE PLAY: No recording, just showing live data on screen.
        if (currentState == AppState.FreePlay)
        {
            if (textInstructions != null)
                textInstructions.text = "<size=150%>Live IMU Mapping</size>";
            if (textLiveScore != null)
                textLiveScore.text = "<color=green>TRACKING ACTIVE</color>\nPress Space to Recalibrate";
            if (avatarSkin != null)
                avatarSkin.material.color = new Color(0.5f, 0.8f, 1f); // Blue avatar

            _uiRefreshTimer -= Time.deltaTime;
            // Only update text UI every 0.1s to save performance.
            if (_uiRefreshTimer <= 0f && textLiveMetrics != null)
            {
                _uiRefreshTimer = 0.1f;

                // Extract Pitch (A/P) and Roll (M/L) from the Chest
                GetOptimizedKinematics(_targetChest, out float cP, out float cR, out float trueChestBend);
                float bP = NormalizeAngle(_targetBicep.eulerAngles.x);
                float bR = NormalizeAngle(_targetBicep.eulerAngles.z);

                textLiveMetrics.text =
                    $"<b>LIVE KINEMATICS</b>\n\n" +
                    $"<color=#00FFFF>CHEST (Absolute)</color>\nPitch: {cP:F1}° | Roll: {cR:F1}°\n\n" +
                    $"<color=#00FF00>CHEST MAGNITUDE</color>\n<b>{trueChestBend:F1}°</b>\n\n" +
                    $"<color=#FF00FF>R BICEP (Absolute)</color>\nPitch: {bP:F1}° | Roll: {bR:F1}°";
            }
            return;
        }

        // Timer increment/decrement logic based on the phase.
        if (currentState == AppState.CalibrationPhase || currentState == AppState.PrepPhase)
            _phaseTimer -= Time.deltaTime; // Count down to 0
        else if (currentState == AppState.ActionPhase)
            _phaseTimer += Time.deltaTime; // Count up indefinitely until ended

        // CALIBRATION PHASE: User must hold still for 2-3 seconds.
        if (currentState == AppState.CalibrationPhase)
        {
            if (textInstructions != null) textInstructions.text = "<size=120%>Auto-Calibration</size>";
            if (textLiveScore    != null) textLiveScore.text    = $"<color=orange>STAND STILL</color>\nZeroing: {Mathf.CeilToInt(_phaseTimer)}s";
            if (avatarSkin       != null) avatarSkin.material.color = new Color(1f, 0.6f, 0f); // Orange avatar

            // When calibration timer hits zero, snap the offsets and move to the next phase.
            if (_phaseTimer <= 0f)
            {
                _needsCalibration = true;
                currentState = (currentMode == AppMode.LiveFreePlay)
                               ? AppState.FreePlay // Skip prep if we're just free-playing
                               : AppState.PrepPhase;
                _phaseTimer = 2f;
            }
        }
        // PREP PHASE: Quick 2-second warning before actual data recording starts.
        else if (currentState == AppState.PrepPhase)
        {
            if (textInstructions != null) textInstructions.text = $"<size=150%>{_activeTestName}</size>";
            if (textLiveScore    != null) textLiveScore.text    = $"<color=yellow>PREPARE</color>\nStarting: {Mathf.CeilToInt(_phaseTimer)}s";
            if (avatarSkin       != null) avatarSkin.material.color = new Color(0.5f, 0.8f, 1f);

            // Time to start recording.
            if (_phaseTimer <= 0f)
            {
                currentState = AppState.ActionPhase;
                _phaseTimer = 0f;
                _testStartTime = DateTime.Now; 
                
                UpdateUIPanels(); 
            }
        }
        // ACTION PHASE: Actively saving data to memory every single frame.
        else if (currentState == AppState.ActionPhase)
        {
            if (textLiveScore != null)
                textLiveScore.text = $"<color=#A020F0>RECORDING</color>\nElapsed: {Mathf.FloorToInt(_phaseTimer)}s";

            if (avatarSkin != null)
                avatarSkin.material.color = new Color(0.8f, 0.4f, 1f); // Purple recording avatar

            // Build the data packet for this specific frame.
            PatientDataPoint dp = new PatientDataPoint
            {
                stepName   = _activeTestName,
                timeStamp  = _phaseTimer,
                sysTimeStr = DateTime.Now.ToString("HH:mm:ss:fff"), 
                chestRot   = _targetChest,
                bicepRot   = _targetBicep,
                leftBicepRot = _targetLeftBicep,
                rightForearmRot = _targetRightForearm,
                leftForearmRot = _targetLeftForearm
            };

            // Add the packet to the gigantic list.
            _patientDataLog.Add(dp);
            // Calculate and store the sway metric specifically from the chest node.
            RecordSwayFrame(_targetChest);

            // Update UI metrics occasionally.
            _uiRefreshTimer -= Time.deltaTime;
            if (_uiRefreshTimer <= 0f) { _uiRefreshTimer = 0.1f; CalculateLiveMetricsAndUpdateUI(); }

            // Redraw the pixel graph.
            DrawGraph();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helper function to extract a float from "Key:Value" string format safely.
    private static bool TryParseToken(string token, out float value)
    {
        value = 0f;
        int colon = token.IndexOf(':');
        if (colon < 0 || colon + 1 >= token.Length) return false;
        return float.TryParse(token.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // Applies the mapping from the UI Dropdowns (e.g., Swapping X and Y).
    private float GetMappedAxis(AxisMap map, float x, float y, float z)
    {
        switch (map)
        {
            case AxisMap.X:    return x;
            case AxisMap.Y:    return y;
            case AxisMap.Z:    return z;
            case AxisMap.NegX: return -x;
            case AxisMap.NegY: return -y;
            case AxisMap.NegZ: return -z;
            default:           return 0f;
        }
    }

    // Forces quaternions to take the shortest path to avoid 360-degree snap spins.
    private static Quaternion EnforceContinuity(Quaternion current, Quaternion next)
    {
        return Quaternion.Dot(current, next) < 0f
               ? new Quaternion(-next.x, -next.y, -next.z, -next.w)
               : next;
    }

    // Converts Unity's 0-360 angles into standard -180 to 180 degree angles.
    private static float NormalizeAngle(float a)
    {
        a %= 360f;
        return a > 180f ? a - 360f : (a < -180f ? a + 360f : a);
    }

    // ══════════════════════════════════════════════════════════════════════
    // UI BUTTON METHODS (Called directly from Canvas Buttons)
    public void GoToDashboard()
    {
        currentMode  = AppMode.None;
        currentState = AppState.Dashboard;
        ResetAvatarsToDefault();
        UpdateUIPanels();
        _forceUIUpdate = true; // Trigger LED UI refresh
    }

    public void OnPortInputSubmitted(string userInput)
    {
        if (string.IsNullOrEmpty(userInput)) return;
        portName = userInput.ToUpper().Trim();
        OpenConnection();
    }

    public void GoToParameterSelection()
    {
        ScaleAvatarToPatient(); // Apply the height input
        currentMode  = AppMode.DataExtraction;
        currentState = AppState.ParameterSelection;
        UpdateUIPanels();
    }

    public void StartLiveFreePlay()
    {
        ScaleAvatarToPatient();
        currentMode  = AppMode.LiveFreePlay;
        _activeTestName = "Live Mapping";
        ResetAvatarsToDefault(); ClearGraph();
        currentState = AppState.CalibrationPhase;
        _phaseTimer  = 3f;
        UpdateUIPanels();
    }

    public void StartPosturalSwayTest()
    {
        _activeTestName = "Postural Sway Assessment";
        ClearTestData(); ResetAvatarsToDefault(); ClearGraph();
        currentState = AppState.CalibrationPhase;
        _phaseTimer  = 3f; _testDuration = 15f;
        UpdateUIPanels();
    }

    public void StartKinematicRecoveryTest()
    {
        _activeTestName = "Kinematic Recovery Strategy";
        ClearTestData(); ResetAvatarsToDefault(); ClearGraph();
        currentState = AppState.CalibrationPhase;
        _phaseTimer  = 3f; _testDuration = 15f;
        UpdateUIPanels();
    }
    
    // Triggered manually when the user presses the "Stop Recording" button.
    public void EndActiveRecording()
    {
        if (currentState == AppState.ActionPhase)
        {
            CalculateSwayMetrics(); // Crunch the final RMS numbers
            GenerateReportCard();   // Show the results screen
        }
    }

    // Wipes memory before starting a new test so arrays don't infinitely grow.
    private void ClearTestData()
    {
        _patientDataLog.Clear();
        _pitchSwayData.Clear();
        _rollSwayData.Clear();
        _combinedSwayData.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Switches which UI Canvas groups are visible based on AppState.
    private void UpdateUIPanels()
    {
        if (panelDashboard  != null) panelDashboard.SetActive(currentState == AppState.Dashboard);
        if (panelSelection  != null) panelSelection.SetActive(currentState == AppState.ParameterSelection);
        if (panelActiveTest != null) panelActiveTest.SetActive(
            currentState == AppState.CalibrationPhase || currentState == AppState.PrepPhase ||
            currentState == AppState.ActionPhase      || currentState == AppState.FreePlay);
        if (panelReport != null) panelReport.SetActive(currentState == AppState.Report);
        if (backButton  != null) backButton.SetActive(currentState  != AppState.Dashboard);

        bool isReport = (currentState == AppState.Report && currentMode == AppMode.DataExtraction);
        if (btnExportCSV   != null) btnExportCSV.SetActive(isReport);
        if (btnExportJSON  != null) btnExportJSON.SetActive(isReport);
        if (btnExportGraph != null) btnExportGraph.SetActive(isReport);
        
        if (btnEndTest != null) 
        {
            btnEndTest.SetActive(currentState == AppState.ActionPhase && currentMode == AppMode.DataExtraction);
        }

        // Hide the 3D avatar completely if we are on the main menu.
        bool showBots = (currentState != AppState.Dashboard);
        if (avatarSkin != null) avatarSkin.enabled = showBots;
        if (liveMat    != null) liveMat.SetActive(showBots);

        // Turn off the native graph during freeplay mode.
        if (graphDisplay    != null) graphDisplay.gameObject.SetActive(currentMode != AppMode.LiveFreePlay);
        if (textLiveMetrics != null) textLiveMetrics.gameObject.SetActive(true);

        // Enable hardware diagnostic LEDs on the side.
        if (chestNodeIndicator   != null) chestNodeIndicator.gameObject.SetActive(true);
        if (bicepNodeIndicator   != null) bicepNodeIndicator.gameObject.SetActive(true);
        if (leftBicepNodeIndicator != null) leftBicepNodeIndicator.gameObject.SetActive(true);
        if (rightForearmNodeIndicator != null) rightForearmNodeIndicator.gameObject.SetActive(true);
        if (leftForearmNodeIndicator != null) leftForearmNodeIndicator.gameObject.SetActive(true);
    }

    // Takes the cm height entered in the UI, divides by standard model height, and scales the Transform.
    private void ScaleAvatarToPatient()
    {
        if (heightInputField == null || string.IsNullOrEmpty(heightInputField.text)) return;
        if (!float.TryParse(heightInputField.text, out float h)) return;

        float s = h / defaultModelHeightCm;
        Vector3 scale = new Vector3(s, s, s);

        if (liveAvatarModel != null)
        {
            liveAvatarModel.transform.localScale = scale;
        }
        else if (liveAvatarRoot != null)
        {
            liveAvatarRoot.localScale = scale;
        }
    }

    // Snaps the character back to a T-pose (or arms down pose) using the default poses defined at the top.
    private void ResetAvatarsToDefault()
    {
        _targetChest       = chestNode == HardwareNode.None ? Quaternion.Euler(chestDefaultPose) : Quaternion.identity;
        _targetBicep       = rightBicepNode == HardwareNode.None ? Quaternion.Euler(rightBicepDefaultPose) : Quaternion.identity;
        _targetLeftBicep   = leftBicepNode == HardwareNode.None ? Quaternion.Euler(leftBicepDefaultPose) : Quaternion.identity;
        _targetRightForearm= rightForearmNode == HardwareNode.None ? Quaternion.Euler(rightForearmDefaultPose) : Quaternion.identity;
        _targetLeftForearm = leftForearmNode == HardwareNode.None ? Quaternion.Euler(leftForearmDefaultPose) : Quaternion.identity;

        _lastChestIn = _lastBicepIn = Quaternion.identity;
        _lastLeftBicepIn = _lastRightForearmIn = _lastLeftForearmIn = Quaternion.identity;
        
        if (liveChestBone   != null) liveChestBone.localRotation   = _targetChest;
        if (liveBicepBone   != null) liveBicepBone.localRotation   = _targetBicep;
        if (liveLeftBicepBone != null) liveLeftBicepBone.localRotation = _targetLeftBicep;
        if (liveRightForearmBone != null) liveRightForearmBone.localRotation = _targetRightForearm;
        if (liveLeftForearmBone != null) liveLeftForearmBone.localRotation = _targetLeftForearm;
        if (avatarSkin      != null) avatarSkin.material.color      = new Color(0.5f, 0.8f, 1f);
    }

    // Slides the camera smoothly between the standard viewpoint and the active recording viewpoint.
    private void HandleCameraMovement()
    {
        if (mainCamera == null || viewStandard == null || viewRecording == null) return;
        Transform target = (currentState != AppState.Dashboard) ? viewRecording : viewStandard;
        mainCamera.position = Vector3.Lerp(mainCamera.position, target.position, Time.deltaTime * cameraSpeed);
        mainCamera.rotation = Quaternion.Slerp(mainCamera.rotation, target.rotation, Time.deltaTime * cameraSpeed);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Extracts specific angles from the Chest rotation and saves them for the clinical metrics.
    private void RecordSwayFrame(Quaternion rot)
    {
        GetOptimizedKinematics(rot, out float pitch, out float roll, out float mag);
        
        // Calculate instantaneous RMS of pitch and roll to get a "combined magnitude of sway".
        float rms = Mathf.Sqrt((pitch*pitch + roll*roll) * 0.5f);
        
        _pitchSwayData.Add(pitch);
        _rollSwayData.Add(roll);
        _combinedSwayData.Add(rms);
    }

    // Calculates current running average/RMS and pushes it to the UI text display.
    private void CalculateLiveMetricsAndUpdateUI()
    {
        if (_pitchSwayData.Count == 0 || textLiveMetrics == null) return;
        GetOptimizedKinematics(_targetChest, out float currentPitch, out float currentRoll, out float _);
        
        float bicepPitch = NormalizeAngle(_targetBicep.eulerAngles.x);
        float bicepRoll  = NormalizeAngle(_targetBicep.eulerAngles.z);
        
        float apRms  = CalculateRMS(_pitchSwayData);
        float mlRms  = CalculateRMS(_rollSwayData);
        float totRms = CalculateRMS(_combinedSwayData);

        textLiveMetrics.text =
            $"<b>LIVE KINEMATICS (Action Phase)</b>\n\n" +
            $"<color=#00FFFF>CHEST (Pitch/Roll):</color> {currentPitch:F1}° / {currentRoll:F1}°\n" +
            $"<color=#FF00FF>R BICEP (Pitch/Roll):</color> {bicepPitch:F1}° / {bicepRoll:F1}°\n\n" +
            $"<color=#AAAAAA><size=80%><i>Running Sway (RMS)</i></size></color>\n" +
            $"<size=90%>A/P: {apRms:F2}° | M/L: {mlRms:F2}°\n" +
            $"<color=#FFFF00>TOTAL: {totRms:F2}°</color></size>";
    }

    // Runs once at the end of a recording to finalize the report card variables.
    private void CalculateSwayMetrics()
    {
        _finalApRMS      = CalculateRMS(_pitchSwayData);
        _finalApPeak     = CalculatePeakToPeak(_pitchSwayData);
        _finalMlRMS      = CalculateRMS(_rollSwayData);
        _finalMlPeak     = CalculatePeakToPeak(_rollSwayData);
        _finalCombinedRMS= CalculateRMS(_combinedSwayData);
    }

    // Pushes the finalized variables into the large text mesh on the Report Card screen.
    private void GenerateReportCard()
    {
        currentState = AppState.Report;
        UpdateUIPanels();
        if (textFinalFeedback == null) return;
        textFinalFeedback.text =
            $"<b><size=120%>CLINICAL REPORT: {_activeTestName}</size></b>\n\n" +
            $"<b>Anterior-Posterior (Pitch):</b>  RMS {_finalApRMS:F2}°  Peak {_finalApPeak:F2}°\n\n" +
            $"<b>Medial-Lateral (Roll):</b>  RMS {_finalMlRMS:F2}°  Peak {_finalMlPeak:F2}°\n\n" +
            $"<b><color=#FFFF00>TOTAL SWAY RMS: {_finalCombinedRMS:F2}°</color></b>";
    }

    // Math function: Finds the absolute difference between the highest and lowest value in a list.
    private float CalculatePeakToPeak(List<float> data)
    {
        float max = float.MinValue, min = float.MaxValue;
        foreach (float v in data) { if (v > max) max = v; if (v < min) min = v; }
        return max - min;
    }

    // Math function: Calculates the Root Mean Square (standard deviation equivalent) of an entire dataset.
    private float CalculateRMS(List<float> data)
    {
        if (data.Count == 0) return 0f;
        float sum = 0f;
        foreach (float v in data) sum += v;
        float mean = sum / data.Count; // Calculate average
        float sqSum = 0f;
        foreach (float v in data) { float d = v - mean; sqSum += d * d; } // Calculate sum of squared differences
        return Mathf.Sqrt(sqSum / data.Count); // Return root of the mean of squared differences
    }

    // ══════════════════════════════════════════════════════════════════════
    // Wipes the graph texture back to dark grey.
    private void ClearGraph()
    {
        if (_graphTexture == null) return;
        Color32[] bg = new Color32[graphWidth * graphHeight];
        Color32 dark = new Color32(20, 20, 25, 120);
        for (int i = 0; i < bg.Length; i++) bg[i] = dark;
        _graphTexture.SetPixels32(bg);
        _graphTexture.Apply();
    }

    // Iterates through the logged sway data and draws pixel lines connecting each point to form the line graph.
    private void DrawGraph()
    {
        if (_graphTexture == null || _pitchSwayData.Count < 2) return;
        ClearGraph();
        
        Color32 grid    = new Color32(50, 50, 60, 255);
        Color32 pitchC  = new Color32(0, 255, 255, 255); // Cyan
        Color32 rollC   = new Color32(255, 0, 255, 255); // Magenta
        Color32 combC   = new Color32(255, 255, 0, 255); // Yellow
        
        int centre = graphHeight / 2;
        // Draw center horizontal grid line (zero point)
        for (int x = 0; x < graphWidth; x++) _graphTexture.SetPixel(x, centre, grid);
        
        int total = _pitchSwayData.Count;
        for (int i = 1; i < total; i++)
        {
            // Map the data index to an X-coordinate on the graph.
            int x0 = Mathf.RoundToInt((float)(i-1)/(total-1)*(graphWidth-1));
            int x1 = Mathf.RoundToInt((float)i      /(total-1)*(graphWidth-1));
            
            // Draw lines for Pitch, Roll, and Combined RMS.
            DrawLine(_graphTexture, x0, MapY(_pitchSwayData[i-1]), x1, MapY(_pitchSwayData[i]),   pitchC);
            DrawLine(_graphTexture, x0, MapY(_rollSwayData[i-1]),  x1, MapY(_rollSwayData[i]),    rollC);
            DrawLine(_graphTexture, x0, MapY(_combinedSwayData[i-1]),x1,MapY(_combinedSwayData[i]),combC);
        }
        _graphTexture.Apply(); // Push pixel changes to GPU.
    }

    // Converts a degree angle (e.g., 5.3 deg) into a Y-pixel coordinate on the graph based on maxGraphAngle.
    private int MapY(float val) =>
        Mathf.RoundToInt(Mathf.Clamp(val/maxGraphAngle, -1f, 1f) * (graphHeight/2f) + (graphHeight/2f));

    // Uses Bresenham's line algorithm to plot a solid line between two pixel coordinates on a texture.
    private void DrawLine(Texture2D tex, int x0, int y0, int x1, int y1, Color32 col)
    {
        int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            // Only draw if within bounds
            if (x0 >= 0 && x0 < tex.width && y0 >= 0 && y0 < tex.height)
                tex.SetPixel(x0, y0, col);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Saves the raw quaternion offsets to the computer's registry (PlayerPrefs) so they persist between app restarts.
    private void SaveCalibration()
    {
        SaveQuat("chestRaw",   _chestCalibRaw);
        SaveQuat("bicepRaw",   _bicepCalibRaw);
        SaveQuat("lBicepRaw",  _leftBicepCalibRaw); 
        SaveQuat("rForeRaw",   _rightForearmCalibRaw);
        SaveQuat("lForeRaw",   _leftForearmCalibRaw);
    
        PlayerPrefs.Save();
    }

    // Loads the stored calibration quaternions from disk.
    private void LoadCalibration()
    {
        if (!PlayerPrefs.HasKey("chestRawW")) return;
        Quaternion chestRaw   = LoadQuat("chestRaw");
        Quaternion bicepRaw   = LoadQuat("bicepRaw");
        Quaternion lBicepRaw  = LoadQuat("lBicepRaw"); 
        Quaternion rForeRaw   = LoadQuat("rForeRaw");
        Quaternion lForeRaw   = LoadQuat("lForeRaw");

        // Failsafe: Prevent loading garbage values if the data was corrupted.
        if (!IsValidQuaternion(chestRaw) || !IsValidQuaternion(bicepRaw))
            return;

        _chestCalibRaw       = chestRaw;
        _bicepCalibRaw       = bicepRaw;
        _leftBicepCalibRaw   = lBicepRaw;
        _rightForearmCalibRaw= rForeRaw;
        _leftForearmCalibRaw = lForeRaw;
    }

    // Checks if a quaternion is mathematically valid (normalized length should be ~1.0).
    private static bool IsValidQuaternion(Quaternion q)
    {
        float sqrMag = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        return sqrMag > 0.5f && sqrMag < 1.5f; 
    }

    // Helper to serialize quaternions into PlayerPrefs.
    private static void SaveQuat(string key, Quaternion q)
    {
        PlayerPrefs.SetFloat(key+"X", q.x); PlayerPrefs.SetFloat(key+"Y", q.y);
        PlayerPrefs.SetFloat(key+"Z", q.z); PlayerPrefs.SetFloat(key+"W", q.w);
    }

    // Helper to deserialize quaternions from PlayerPrefs.
    private static Quaternion LoadQuat(string key) =>
        new Quaternion(PlayerPrefs.GetFloat(key+"X"), PlayerPrefs.GetFloat(key+"Y"),
                       PlayerPrefs.GetFloat(key+"Z"), PlayerPrefs.GetFloat(key+"W"));

    // ══════════════════════════════════════════════════════════════════════
    // EXPORT: Formats the entire test session into a Comma Separated Values file.
    public void ExportPatientDataCSV()
    {
        string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // Opens the OS-native file saving dialog box.
        string path = StandaloneFileBrowser.SaveFilePanel(
            "Save CSV", "", $"PatientData_{_activeTestName.Replace(" ","")}_{ts}", "csv");
        if (string.IsNullOrEmpty(path)) return;

        var sb = new System.Text.StringBuilder();
        // Write header information and final clinical metrics.
        sb.AppendLine($"Test Name,{_activeTestName}");
        sb.AppendLine($"AP_RMS,{_finalApRMS:F4},AP_PEAK,{_finalApPeak:F4}");
        sb.AppendLine($"ML_RMS,{_finalMlRMS:F4},ML_PEAK,{_finalMlPeak:F4}");
        sb.AppendLine($"TOTAL_SWAY_RMS,{_finalCombinedRMS:F4}");
        sb.AppendLine();
        
        // Write the columns for the frame-by-frame raw data.
        sb.AppendLine("SystemTime,RelativeTime_Sec,Chest_Qx,Chest_Qy,Chest_Qz,Chest_Qw," +
                      "Chest_ForwardBend_X,Chest_RightBend_Z,RightBicep_Pitch_X,LeftBicep_Pitch_X,RightForearm_Pitch_X,LeftForearm_Pitch_X");
        
        // Iterate through the giant log and append every single frame.
        foreach (var dp in _patientDataLog)
        {
            Vector3 cE  = dp.chestRot.eulerAngles;
            Vector3 rbE = dp.bicepRot.eulerAngles;
            Vector3 lbE = dp.leftBicepRot.eulerAngles; 
            Vector3 rfE = dp.rightForearmRot.eulerAngles;
            Vector3 lfE = dp.leftForearmRot.eulerAngles;
            
            sb.AppendLine($"{dp.sysTimeStr},{dp.timeStamp:F2}," +
                          $"{dp.chestRot.x:F4},{dp.chestRot.y:F4},{dp.chestRot.z:F4},{dp.chestRot.w:F4}," +
                          $"{NormalizeAngle(cE.x):F2},{NormalizeAngle(cE.z):F2}," +
                          $"{NormalizeAngle(rbE.x):F2},{NormalizeAngle(lbE.x):F2}," +
                          $"{NormalizeAngle(rfE.x):F2},{NormalizeAngle(lfE.x):F2}"); 
        }

        try
        {
            // Push the giant string to the hard drive.
            System.IO.File.WriteAllText(path, sb.ToString());
            if (textFinalFeedback != null)
                textFinalFeedback.text += $"\n\n<color=green>CSV SAVED:</color>\n<size=60%>{path}</size>";
        }
        catch (Exception e)
        {
            if (textFinalFeedback != null)
                textFinalFeedback.text += $"\n\n<color=red>SAVE ERROR:</color> {e.Message}";
        }
    }

    // EXPORT: Formats the entire test session into JSON (JavaScript Object Notation) for programmatic parsing.
    public void ExportPatientDataJSON()
    {
        if (_patientDataLog.Count == 0) return;

        string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = StandaloneFileBrowser.SaveFilePanel(
            "Save JSON", "", $"PatientData_{_activeTestName.Replace(" ", "")}_{ts}", "json");
        
        if (string.IsNullOrEmpty(path)) return;

        var sb = new System.Text.StringBuilder();
        
        // Construct the root object and metrics block.
        sb.AppendLine("{");
        sb.AppendLine($"  \"testName\": \"{_activeTestName}\",");
        sb.AppendLine($"  \"metrics\": {{");
        sb.AppendLine($"    \"apRms\": {_finalApRMS:F4},");
        sb.AppendLine($"    \"apPeak\": {_finalApPeak:F4},");
        sb.AppendLine($"    \"mlRms\": {_finalMlRMS:F4},");
        sb.AppendLine($"    \"mlPeak\": {_finalMlPeak:F4},");
        sb.AppendLine($"    \"totalSwayRms\": {_finalCombinedRMS:F4}");
        sb.AppendLine($"  }},");
        sb.AppendLine($"  \"frames\": [");

        // Iterate and construct JSON objects for each frame.
        for (int i = 0; i < _patientDataLog.Count; i++)
        {
            var dp = _patientDataLog[i];
            
            Vector3 cE  = dp.chestRot.eulerAngles;
            Vector3 bE  = dp.bicepRot.eulerAngles;
            Vector3 lbE = dp.leftBicepRot.eulerAngles;
            Vector3 rfE = dp.rightForearmRot.eulerAngles;
            Vector3 lfE = dp.leftForearmRot.eulerAngles;

            float chestFwdBend = NormalizeAngle(cE.x);
            float chestRightBend = NormalizeAngle(cE.z);
            float bicepPitch = NormalizeAngle(bE.x);
            float bicepRoll = NormalizeAngle(bE.z);

            sb.AppendLine("    {");
            sb.AppendLine($"      \"sysTime\": \"{dp.sysTimeStr}\",");
            sb.AppendLine($"      \"relTime_Sec\": {dp.timeStamp:F3},");
            sb.AppendLine($"      \"chestQuat\": {{\"x\": {dp.chestRot.x:F4}, \"y\": {dp.chestRot.y:F4}, \"z\": {dp.chestRot.z:F4}, \"w\": {dp.chestRot.w:F4}}},");
            sb.AppendLine($"      \"chestAngles\": {{\"forwardBendX\": {chestFwdBend:F2}, \"rightBendZ\": {chestRightBend:F2}}},");
            sb.AppendLine($"      \"bicepAngles\": {{\"pitchX\": {bicepPitch:F2}, \"rollZ\": {bicepRoll:F2}}},");
            sb.AppendLine($"      \"leftBicepAngles\": {{\"pitchX\": {NormalizeAngle(lbE.x):F2}, \"rollZ\": {NormalizeAngle(lbE.z):F2}}},");
            sb.AppendLine($"      \"rightForearmAngles\": {{\"pitchX\": {NormalizeAngle(rfE.x):F2}, \"rollZ\": {NormalizeAngle(rfE.z):F2}}},");
            sb.AppendLine($"      \"leftForearmAngles\": {{\"pitchX\": {NormalizeAngle(lfE.x):F2}, \"rollZ\": {NormalizeAngle(lfE.z):F2}}}");
            
            if (i < _patientDataLog.Count - 1)
                sb.AppendLine("    },"); // Add comma if not the last item in array
            else
                sb.AppendLine("    }");
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");

        try
        {
            System.IO.File.WriteAllText(path, sb.ToString());
            if (textFinalFeedback != null)
                textFinalFeedback.text += $"\n\n<color=green>JSON SAVED:</color>\n<size=60%>{path}</size>";
        }
        catch (Exception e)
        {
            if (textFinalFeedback != null)
                textFinalFeedback.text += $"\n\n<color=red>SAVE ERROR:</color> {e.Message}";
        }
    }

    // EXPORT: Renders a high-resolution version of the graph texture and saves it as an image file.
    public void ExportGraphPNG()
    {
        if (_pitchSwayData.Count < 2) return;
        string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = StandaloneFileBrowser.SaveFilePanel(
            "Save Graph PNG", "", $"Graph_{_activeTestName.Replace(" ","")}_{ts}", "png");
        if (string.IsNullOrEmpty(path)) return;

        // Create a new, larger texture exclusively for the export file.
        int eW = 1200, eH = 600;
        Texture2D exp = new Texture2D(eW, eH, TextureFormat.RGB24, false);
        Color32 bg = new Color32(20, 20, 25, 255); // Dark background
        for (int y = 0; y < eH; y++) for (int x = 0; x < eW; x++) exp.SetPixel(x, y, bg);

        // Define margin space for text labels around the edges.
        int mL=80, mR=40, mT=80, mB=60;
        int gW=eW-mL-mR, gH=eH-mT-mB;

        Color32 grid    = new Color32(70, 70, 80, 255);
        Color32 textCol = new Color32(220, 220, 220, 255);
        Color32 pC      = new Color32(0, 255, 255, 255);
        Color32 rC      = new Color32(255, 0, 255, 255);
        Color32 cC      = new Color32(255, 255, 0, 255);

        // Draw boundaries and zero-line for the chart area.
        for(int x=mL; x<=mL+gW; x++) { exp.SetPixel(x, mB+gH/2, grid); exp.SetPixel(x, mB+gH, grid); exp.SetPixel(x, mB, grid); }
        for(int y=mB; y<=mB+gH; y++) exp.SetPixel(mL, y, grid);

        int scale = 2;
        int Ymap(float v, int h) => mB + Mathf.RoundToInt(Mathf.Clamp(v / maxGraphAngle, -1f, 1f) * (h / 2f) + (h / 2f));

        // Draw Y-Axis labels.
        DrawString(exp, 10, Ymap(maxGraphAngle, gH) - 5, $"+{maxGraphAngle} DEG", textCol, scale);
        DrawString(exp, 10, Ymap(0, gH) - 5, "  0 DEG", textCol, scale);
        DrawString(exp, 10, Ymap(-maxGraphAngle, gH) - 5, $"-{maxGraphAngle} DEG", textCol, scale);

        // Draw X-Axis Time labels based on the actual recorded duration.
        float totalTime = _patientDataLog.Count > 0 ? _patientDataLog[_patientDataLog.Count - 1].timeStamp : _testDuration;
        int timeSteps = 5;
        for (int i = 0; i <= timeSteps; i++)
        {
            float t = (totalTime / timeSteps) * i;
            int xPos = mL + (gW / timeSteps) * i;

            // Draw little tick marks.
            for(int y = mB - 5; y <= mB + 5; y++) exp.SetPixel(xPos, y, grid);
            
            // Draw time string.
            DateTime tickTime = _testStartTime.AddSeconds(t);
            DrawString(exp, xPos - 25, mB - 25, tickTime.ToString("HH:mm:ss"), textCol, scale);
        }

        // Draw the color legend at the bottom.
        DrawString(exp, mL, eH - 40, "LEGEND:", textCol, scale);
        DrawString(exp, mL + 100, eH - 40, "- PITCH (A/P)", pC, scale);
        DrawString(exp, mL + 320, eH - 40, "- ROLL (M/L)", rC, scale);
        DrawString(exp, mL + 520, eH - 40, "- TOTAL SWAY", cC, scale);

        // Plot the actual data points.
        int tot = _pitchSwayData.Count;
        for (int i = 1; i < tot; i++)
        {
            int x0 = mL + Mathf.RoundToInt((float)(i - 1) / (tot - 1) * gW);
            int x1 = mL + Mathf.RoundToInt((float)i / (tot - 1) * gW);
            DrawLine(exp, x0, Ymap(_pitchSwayData[i-1], gH),   x1, Ymap(_pitchSwayData[i], gH),   pC);
            DrawLine(exp, x0, Ymap(_rollSwayData[i-1], gH),    x1, Ymap(_rollSwayData[i], gH),    rC);
            DrawLine(exp, x0, Ymap(_combinedSwayData[i-1], gH),x1, Ymap(_combinedSwayData[i], gH),cC);
        }

        // Push pixels, encode to PNG byte array, and clean up the temporary texture.
        exp.Apply();
        byte[] bytes = exp.EncodeToPNG();
        Destroy(exp);

        try { System.IO.File.WriteAllBytes(path, bytes); }
        catch (Exception e)
        {
            if (textFinalFeedback != null)
                textFinalFeedback.text += $"\n<color=red>PNG ERROR:</color> {e.Message}";
        }
    }

    // Safety fallback: Close the serial port to prevent locking the COM port if the app crashes or closes.
    void OnApplicationQuit()
    {
        if (_serial != null && _serial.IsOpen) _serial.Close();
        _serialOpen = false;
    }
    void OnDestroy()
    {
        if (_serial != null && _serial.IsOpen) _serial.Close();
        _serialOpen = false;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Custom mathematical function to extract stable Euler angles (Pitch/Roll) from a Quaternion,
    // avoiding standard Gimbal Lock issues that Unity's `eulerAngles` property suffers from.
    private void GetOptimizedKinematics(Quaternion relativeRot, out float optPitch, out float optRoll, out float magnitude)
    {
        // Extract raw pitch and roll mathematically.
        float rawPitch = Mathf.Atan2(2f * (relativeRot.w * relativeRot.x + relativeRot.y * relativeRot.z),
                                     1f - 2f * (relativeRot.x * relativeRot.x + relativeRot.y * relativeRot.y)) * Mathf.Rad2Deg;
        float rawRoll  = Mathf.Atan2(2f * (relativeRot.w * relativeRot.z + relativeRot.x * relativeRot.y),
                                     1f - 2f * (relativeRot.y * relativeRot.y + relativeRot.z * relativeRot.z)) * Mathf.Rad2Deg;
        
        // Find total deviation from zero-pose.
        float pureMagnitude = Quaternion.Angle(Quaternion.identity, relativeRot);
        
        // Heuristic scales to adjust for human bio-mechanics slightly.
        float posPitchScale = 1.04f;
        float negPitchScale = 0.99f;
        float posRollScale  = 1.00f;
        float negRollScale  = 1.00f;
        float activeScale = 1.0f;
        if (Mathf.Abs(rawPitch) > Mathf.Abs(rawRoll)) { activeScale = (rawPitch > 0) ? posPitchScale : negPitchScale; }
        else { activeScale = (rawRoll > 0) ? posRollScale : negRollScale; }

        magnitude = pureMagnitude * activeScale;
        float totalAng = Mathf.Sqrt(rawPitch * rawPitch + rawRoll * rawRoll);
        // If angle is negligible, return flat zero to avoid floating point noise.
        if (totalAng < 0.1f) { optPitch = 0f; optRoll = 0f; return; }
        
        // Re-scale the angles based on magnitude ratios.
        float pitchRatio = Mathf.Abs(rawPitch) / totalAng;
        float rollRatio  = Mathf.Abs(rawRoll) / totalAng;
        optPitch = Mathf.Sign(rawPitch) * (magnitude * pitchRatio);
        optRoll  = Mathf.Sign(rawRoll)  * (magnitude * rollRatio);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Takes a text string and calls the DrawChar function for each character to "type" text onto the Texture2D.
    private void DrawString(Texture2D tex, int x, int y, string text, Color32 col, int scale = 1)
    {
        int cursorX = x;
        text = text.ToUpper();
        foreach (char c in text)
        {
            if (c == ' ') { cursorX += 4 * scale; continue; } // Space logic
            DrawChar(tex, cursorX, y, c, col, scale);
            cursorX += 4 * scale; // Move cursor right for next character
        }
    }

    // Interprets the binary array returned by GetCharMap and literally draws the pixels onto the Texture2D.
    private void DrawChar(Texture2D tex, int x, int y, char c, Color32 col, int scale)
    {
        string[] fontMap = GetCharMap(c);
        if (fontMap == null) return;
        
        // Loop through the 5 rows and 3 columns of the pixel font character.
        for (int r = 0; r < 5; r++)
        {
            for (int ci = 0; ci < 3; ci++)
            {
                if (fontMap[r][ci] == '1') // If the map says '1', plot a pixel (or a block of pixels if scaled).
                {
                    for(int sx = 0; sx < scale; sx++)
                    {
                        for(int sy = 0; sy < scale; sy++)
                        {
                            tex.SetPixel(x + ci * scale + sx, y + (4 - r) * scale + sy, col);
                        }
                    }
                }
            }
        }
    }

    // A hardcoded 3x5 pixel font map used because Unity doesn't have a native way to write text onto a Raw Texture.
    // '1' means pixel is colored, '0' means transparent.
    private string[] GetCharMap(char c)
    {
        switch (c)
        {
            case '0': return new[] { "111", "101", "101", "101", "111" };
            case '1': return new[] { "010", "110", "010", "010", "111" };
            case '2': return new[] { "111", "001", "111", "100", "111" };
            case '3': return new[] { "111", "001", "111", "001", "111" };
            case '4': return new[] { "101", "101", "111", "001", "001" };
            case '5': return new[] { "111", "100", "111", "001", "111" };
            case '6': return new[] { "111", "100", "111", "101", "111" };
            case '7': return new[] { "111", "001", "010", "010", "010" };
            case '8': return new[] { "111", "101", "111", "101", "111" };
            case '9': return new[] { "111", "101", "111", "001", "111" };
            case 'A': return new[] { "111", "101", "111", "101", "101" };
            case 'B': return new[] { "110", "101", "110", "101", "110" };
            case 'C': return new[] { "111", "100", "100", "100", "111" };
            case 'D': return new[] { "110", "101", "101", "101", "110" };
            case 'E': return new[] { "111", "100", "111", "100", "111" };
            case 'F': return new[] { "111", "100", "111", "100", "100" };
            case 'G': return new[] { "111", "100", "101", "101", "111" };
            case 'H': return new[] { "101", "101", "111", "101", "101" };
            case 'I': return new[] { "111", "010", "010", "010", "111" };
            case 'J': return new[] { "001", "001", "001", "101", "111" };
            case 'K': return new[] { "101", "110", "100", "110", "101" };
            case 'L': return new[] { "100", "100", "100", "100", "111" };
            case 'M': return new[] { "101", "111", "101", "101", "101" };
            case 'N': return new[] { "111", "101", "101", "101", "101" };
            case 'O': return new[] { "111", "101", "101", "101", "111" };
            case 'P': return new[] { "111", "101", "111", "100", "100" };
            case 'Q': return new[] { "111", "101", "101", "111", "001" };
            case 'R': return new[] { "111", "101", "110", "101", "101" };
            case 'S': return new[] { "111", "100", "111", "001", "111" };
            case 'T': return new[] { "111", "010", "010", "010", "010" };
            case 'U': return new[] { "101", "101", "101", "101", "111" };
            case 'V': return new[] { "101", "101", "101", "101", "010" };
            case 'W': return new[] { "101", "101", "101", "111", "101" };
            case 'X': return new[] { "101", "101", "010", "101", "101" };
            case 'Y': return new[] { "101", "101", "010", "010", "010" };
            case 'Z': return new[] { "111", "001", "010", "100", "111" };
            case '-': return new[] { "000", "000", "111", "000", "000" };
            case '+': return new[] { "000", "010", "111", "010", "000" };
            case '.': return new[] { "000", "000", "000", "000", "010" };
            case ':': return new[] { "000", "010", "000", "010", "000" };
            case '(': return new[] { "010", "100", "100", "100", "010" };
            case ')': return new[] { "010", "001", "001", "001", "010" };
            case '/': return new[] { "001", "001", "010", "100", "100" };
            default:  return new[] { "111", "101", "101", "101", "111" };
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Wipes serial data and forces the UI LED lights to redraw if they get stuck.
    public void ForceDashboardRefresh()
    {
        if (_serial != null && _serial.IsOpen) { _serial.DiscardInBuffer(); }
        _rxBuffer.Clear();
        _forceUIUpdate = true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // UI DROPDOWN EVENT HOOKS
    // These functions are bound to the actual TMP_Dropdown UI elements in Unity.
    // They dynamically pass the selected ID (e.g., 2 for Gamma) into the variables handling hardware mapping.
    public void SetChestNode(int val)        { chestNode = (HardwareNode)val; }
    public void SetRightBicepNode(int val)   { rightBicepNode = (HardwareNode)val; }
    public void SetLeftBicepNode(int val)    { leftBicepNode = (HardwareNode)val; }
    public void SetRightForearmNode(int val) { rightForearmNode = (HardwareNode)val; }
    public void SetLeftForearmNode(int val)  { leftForearmNode = (HardwareNode)val; }
}

// ============================================================
//  INSPECTOR BUTTON — Auto-Style Dropdowns
// ============================================================
#if UNITY_EDITOR // Prevents this Editor-only code from crashing a compiled Windows Build.
[UnityEditor.CustomEditor(typeof(SynchroKin))]
public class SynchroKinInspector : UnityEditor.Editor
{
    // Forces the inspector window to redraw constantly.
    public override bool RequiresConstantRepaint()
    {
        return true;
    }

    public override void OnInspectorGUI()
    {
        // Draw the normal SynchroKin variables first.
        DrawDefaultInspector();

        SynchroKin script = (SynchroKin)target;

        // Add a blank space and a bold header for our custom button section.
        UnityEditor.EditorGUILayout.Space(10);
        UnityEditor.EditorGUILayout.LabelField("Dropdown Styling Tools", UnityEditor.EditorStyles.boldLabel);

        // Render the actual button you click in the Unity Editor.
        if (GUILayout.Button("Auto-Style All Node Dropdowns", GUILayout.Height(30)))
        {
            // If clicked, it iterates through all 5 dropdown variables and applies the styling template.
            StyleDropdownEditor(script.dropdownChest, script);
            StyleDropdownEditor(script.dropdownRightBicep, script);
            StyleDropdownEditor(script.dropdownLeftBicep, script);
            StyleDropdownEditor(script.dropdownRightForearm, script);
            StyleDropdownEditor(script.dropdownLeftForearm, script);

            Debug.Log("[SynchroKinInspector] Dropdown styling applied to all assigned dropdowns.");
        }

        // Draw an informative blue Help Box underneath the button explaining what it does.
        UnityEditor.EditorGUILayout.HelpBox(
            "Resizes the popup box (width + height), item rows, fonts, and colors " +
            "for all 5 node dropdowns using the values set in 'Dropdown Styling' above. " +
            "Click any time after changing those values, or after re-assigning a dropdown reference.",
            UnityEditor.MessageType.Info);
    }

    // The giant function that manually overrides Unity's complex TMP_Dropdown hierarchy settings.
    private void StyleDropdownEditor(TMP_Dropdown dd, SynchroKin script)
    {
        if (dd == null) return;
        if (dd.template == null) return;

        // Records the initial state so you can hit 'Ctrl+Z' in Unity if you make a mistake.
        UnityEditor.Undo.RecordObject(dd.template, "Style Dropdown Template");

        // Disable horizontal/vertical fitters on the main template so we can strictly enforce our own sizes.
        var templateFitter = dd.template.GetComponent<UnityEngine.UI.ContentSizeFitter>();
        if (templateFitter != null)
        {
            UnityEditor.Undo.RecordObject(templateFitter, "Disable Template ContentSizeFitter");
            templateFitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
            templateFitter.verticalFit   = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
            UnityEditor.EditorUtility.SetDirty(templateFitter); // Tells Unity to save this change.
        }

        // Apply width and height to the dropdown popup box.
        Vector2 templateSize = dd.template.sizeDelta;
        templateSize.x = script.dropdownTemplateWidth;
        templateSize.y = script.dropdownTemplateHeight;
        dd.template.sizeDelta = templateSize;
        UnityEditor.EditorUtility.SetDirty(dd.template);

        // Apply background color to the popup box.
        var templateBg = dd.template.GetComponent<UnityEngine.UI.Image>();
        if (templateBg != null)
        {
            UnityEditor.Undo.RecordObject(templateBg, "Recolor Dropdown Template Background");
            templateBg.color = script.dropdownBackgroundColor;
            UnityEditor.EditorUtility.SetDirty(templateBg);
        }

        // Find the 'Item' prefab template inside the dropdown structure.
        RectTransform itemRect = dd.template.Find("Viewport/Content/Item") as RectTransform;
        if (itemRect != null)
        {
            // Turn off layout fitters on the individual item row.
            var itemFitter = itemRect.GetComponent<UnityEngine.UI.ContentSizeFitter>();
            if (itemFitter != null)
            {
                UnityEditor.Undo.RecordObject(itemFitter, "Disable Item ContentSizeFitter");
                itemFitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                itemFitter.verticalFit   = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                UnityEditor.EditorUtility.SetDirty(itemFitter);
            }

            // Force anchors so the item row stretches fully across the width of the popup box.
            UnityEditor.Undo.RecordObject(itemRect, "Resize Dropdown Item");
            itemRect.anchorMin = new Vector2(0f, itemRect.anchorMin.y);
            itemRect.anchorMax = new Vector2(1f, itemRect.anchorMax.y);
            itemRect.offsetMin = new Vector2(0f, itemRect.offsetMin.y);
            itemRect.offsetMax = new Vector2(0f, itemRect.offsetMax.y);
            
            // Apply height limit to individual row.
            Vector2 size = itemRect.sizeDelta;
            size.y = script.dropdownItemHeight;
            itemRect.sizeDelta = size;
            UnityEditor.EditorUtility.SetDirty(itemRect);

            // Recolor individual item background.
            var itemBg = itemRect.Find("Item Background")?.GetComponent<UnityEngine.UI.Image>();
            if (itemBg != null)
            {
                UnityEditor.Undo.RecordObject(itemBg, "Recolor Dropdown Item Background");
                itemBg.color = script.dropdownBackgroundColor;
                UnityEditor.EditorUtility.SetDirty(itemBg);
            }

            // Recolor the little checkmark icon.
            var checkmark = itemRect.Find("Item Checkmark")?.GetComponent<UnityEngine.UI.Image>();
            if (checkmark != null)
            {
                UnityEditor.Undo.RecordObject(checkmark, "Recolor Dropdown Checkmark");
                checkmark.color = script.dropdownFontColor;
                UnityEditor.EditorUtility.SetDirty(checkmark);
            }
        }

        // Adjust the Viewport rect to fill the template completely without offsets.
        RectTransform viewportRect = dd.template.Find("Viewport") as RectTransform;
        if (viewportRect != null)
        {
            UnityEditor.Undo.RecordObject(viewportRect, "Resize Dropdown Viewport");
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            UnityEditor.EditorUtility.SetDirty(viewportRect);
        }
        
        // Adjust the inner Content rect (which holds the vertical list of items).
        RectTransform contentRect = dd.template.Find("Viewport/Content") as RectTransform;
        if (contentRect != null)
        {
            var contentFitter = contentRect.GetComponent<UnityEngine.UI.ContentSizeFitter>();
            if (contentFitter != null)
            {
                UnityEditor.Undo.RecordObject(contentFitter, "Disable Content ContentSizeFitter");
                contentFitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                contentFitter.verticalFit   = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                UnityEditor.EditorUtility.SetDirty(contentFitter);
            }

            // Stretch horizontal bounds.
            UnityEditor.Undo.RecordObject(contentRect, "Widen Dropdown Content");
            contentRect.anchorMin = new Vector2(0f, contentRect.anchorMin.y);
            contentRect.anchorMax = new Vector2(1f, contentRect.anchorMax.y);
            contentRect.offsetMin = new Vector2(0f, contentRect.offsetMin.y);
            contentRect.offsetMax = new Vector2(0f, contentRect.offsetMax.y);
            UnityEditor.EditorUtility.SetDirty(contentRect);
        }

        // Apply Custom Fonts and disable AutoSizing for the Item label text.
        TextMeshProUGUI itemLabel = dd.itemText as TextMeshProUGUI;
        if (itemLabel != null)
        {
            UnityEditor.Undo.RecordObject(itemLabel, "Resize Dropdown Item Font");
            itemLabel.enableAutoSizing = false;
            itemLabel.fontSize = script.dropdownItemFontSize;
            itemLabel.color = script.dropdownFontColor;
            UnityEditor.EditorUtility.SetDirty(itemLabel);
        }

        // Apply Custom Fonts and disable AutoSizing for the main display box (Caption) text.
        TextMeshProUGUI captionLabel = dd.captionText as TextMeshProUGUI;
        if (captionLabel != null)
        {
            UnityEditor.Undo.RecordObject(captionLabel, "Resize Dropdown Caption Font");
            captionLabel.enableAutoSizing = false;
            captionLabel.fontSize = script.dropdownCaptionFontSize;
            captionLabel.color = script.dropdownFontColor;
            UnityEditor.EditorUtility.SetDirty(captionLabel);
        }

        // Apply background color to the collapsed drop box (before it's clicked).
        var ddBg = dd.GetComponent<UnityEngine.UI.Image>();
        if (ddBg != null)
        {
            UnityEditor.Undo.RecordObject(ddBg, "Recolor Dropdown Box Background");
            ddBg.color = script.dropdownBackgroundColor;
            UnityEditor.EditorUtility.SetDirty(ddBg);
        }

        // Disable 'Child Control Height' so we can force our custom row heights cleanly.
        Transform contentTransform = dd.template.Find("Viewport/Content");
        if (contentTransform != null)
        {
            var layoutGroup = contentTransform.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            if (layoutGroup != null)
            {
                UnityEditor.Undo.RecordObject(layoutGroup, "Disable Layout Height Control");
                layoutGroup.childControlHeight = false;
                UnityEditor.EditorUtility.SetDirty(layoutGroup);
            }
        }

        // Mark the entire template object as dirty so Unity saves it properly.
        UnityEditor.EditorUtility.SetDirty(dd.template.gameObject);

        // Tell Unity that the current scene has unsaved changes.
        if (!Application.isPlaying)
        {
            UnityEngine.SceneManagement.Scene scene = dd.gameObject.scene;
            if (scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
#endif