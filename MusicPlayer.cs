using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ExitGames.Client.Photon;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

// ============================================================
// MUSIC PLAYER CORE (UI-independent)
// 
// Все UI элементы назначаются через инспектор.
// Вы можете создать свой собственный интерфейс в любой стилистике.
// Экземпляр добавляется в сцену вручную (авто-создания НЕТ):
// Tools -> Music Player -> Add To Current Scene или вручную
// через Add Component в любую сцену.
//
// Основные компоненты для назначения:
// - AudioSource
// - UI кнопки: Play/Pause, Next, Prev, Collapse, Disable, AddTrack
// - UI Dropdown: выбор трека (заполняется кодом из Resources/Music)
// - UI тексты: Title, Time, Volume
// - UI слайдеры: Seek, Volume
// - UI тогглы: Music, Sync
// - UI панели: Main Panel (обычная, все контролы), Collapsed Restore
//   (кнопка Restore - видна только когда свёрнуто), Disabled Panel
//   (панель для восстановления при выключении)
//
// Сворачивание: всё скрыто, остаётся только кнопка Restore.
// Расширенных панелей нет - только обычная и свёрнутая.
//
// Добавление трека (Add Track): в редакторе и на Windows открывается
// системный проводник; на Android - список аудиофайлов устройства.
// Выбранный файл копируется в persistentDataPath/Music и загружается
// в плейлист (переживает перезапуск).
//
// Глушение музыки у других игроков: публичные методы
// MuteOthersMusic() / UnmuteOthersMusic() - разовая команда ВСЕМ другим
// игрокам в комнате остановить/возобновить музыку, независимо от их
// тумблера Sync (аналог старого "бана", но без списков и блокировок).
// ============================================================
[DisallowMultipleComponent]
public class MusicPlayer : MonoBehaviour
{
    public static MusicPlayer Instance { get; private set; }

    private const byte MUSIC_EVENT_CODE = 199;
    private const string KEY_VOL = "MusicPlayer.Volume";
    private const string KEY_MUSIC = "MusicPlayer.MusicOn";
    private const string KEY_SYNC = "MusicPlayer.Sync";
    private const string KEY_ENABLED = "MusicPlayer.Enabled";
    private const string KEY_ADDED = "MusicPlayer.Added";

    // ============================================================
    // UI References (назначаются в инспекторе)
    // ============================================================
    [Header("=== AUDIO SOURCE ===")]
    [SerializeField] private AudioSource audioSource;

    [Header("=== UI BUTTONS ===")]
    [SerializeField] private Button playPauseButton;
    [SerializeField] private Button nextButton;
    [SerializeField] private Button prevButton;
    [SerializeField] private Button collapseButton;
    [SerializeField] private Button disableButton;
    [SerializeField] private Button restoreButton;
    [SerializeField] private Button addTrackButton;          // Открывает проводник / список аудио на устройстве

    [Header("=== UI TEXTS ===")]
    [SerializeField] private Text titleText;
    [SerializeField] private Text timeText;
    [SerializeField] private Text volumeText;

    [Header("=== UI SLIDERS ===")]
    [SerializeField] private Slider seekSlider;
    [SerializeField] private Slider volumeSlider;

    [Header("=== UI TOGGLES ===")]
    [SerializeField] private Toggle musicToggle;
    [SerializeField] private Toggle syncToggle;

    [Header("=== UI DROPDOWN ===")]
    [Tooltip("Выбор трека. Заполняется кодом при загрузке плейлиста.")]
    [SerializeField] private Dropdown trackDropdown;

    [Header("=== UI PANELS ===")]
    [Tooltip("Обычная панель со всеми контролами")]
    [SerializeField] private GameObject mainPanel;
    [Tooltip("Кнопка Restore (свёрнуто: всё скрыто кроме неё)")]
    [SerializeField] private GameObject collapsedRestore;
    [Tooltip("Панель для восстановления (выключено)")]
    [SerializeField] private GameObject disabledPanel;

    [Header("=== PLAY/PAUSE ICON (optional) ===")]
    [Tooltip("Спрайт кнопки Play/Pause, пока музыка ИГРАЕТ (значок паузы). Если не назначен - используется текст \"⏸\"/\"▶\"")]
    [SerializeField] private Sprite playingIconSprite;
    [Tooltip("Спрайт кнопки Play/Pause, когда музыка НА ПАУЗЕ (значок play)")]
    [SerializeField] private Sprite pausedIconSprite;

    [Header("=== MENU MUSIC FADE ===")]
    [Tooltip("AudioSource фоновой музыки меню. Плавно затухает, когда плеер начинает играть, и плавно возвращается при паузе/остановке")]
    [SerializeField] private AudioSource menuMusicSource;
    [Tooltip("Длительность затухания/возврата фоновой музыки меню, секунды")]
    [SerializeField] private float menuMusicFadeSeconds = 1.5f;

    [Header("=== MUSIC SOURCES ===")]
    [Tooltip("Optional clips assigned directly in the Inspector")]
    [SerializeField] private AudioClip[] defaultMusic;

    // ============================================================
    // State
    // ============================================================
    private class TrackInfo
    {
        public readonly string id;
        public readonly AudioClip clip;
        public TrackInfo(string id, AudioClip clip) { this.id = id; this.clip = clip; }
    }

    private readonly List<TrackInfo> _tracks = new List<TrackInfo>();
    private readonly List<string> _addedFiles = new List<string>();
    private int _currentTrackIndex = -1;
    private string _currentTrackId = null;
    private bool _started;
    private bool _pausedByUser;
    private bool _seeking;
    private float _pendingSeek;
    private float _lastTimeLabelUpdate;
    private bool _wasPlayingBeforePause;

    private int _volumePct = 70;
    private bool _musicEnabled = true;
    private bool _syncEnabled;
    private bool _playerEnabled = true;
    private bool _collapsed;

    private AudioSource _audio;
    private bool _refreshingDropdown;
    private Coroutine _menuMusicFade;
    private float _menuMusicBaseVolume = -1f;
    private bool _lastPlayingState;

    private static readonly RaiseEventOptions _othersOpts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };

    // ============================================================
    // Lifecycle
    // ============================================================
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.Log("[MusicPlayer]: Duplicate instance destroyed");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        if (GetComponent<RectTransform>() != null)
            EnsureOwnCanvas();
        DontDestroyOnLoad(transform.root.gameObject);

        _audio = audioSource != null ? audioSource : GetComponent<AudioSource>();
        if (_audio == null) _audio = gameObject.AddComponent<AudioSource>();
        _audio.playOnAwake = false;
        _audio.loop = false;
        _audio.spatialBlend = 0f;

        LoadPrefs();
        ApplyVolume();
        LoadPlaylist();
        StartCoroutine(LoadAddedTracks());

        // Настройка UI
        SetupUI();
        RefreshUIState();

        PhotonNetwork.OnEventCall += OnPhotonEvent;
        Debug.Log("[MusicPlayer]: Initialized. Tracks=" + _tracks.Count + ", sync=" + (_syncEnabled ? "ON" : "OFF") + ", music=" + (_musicEnabled ? "ON" : "OFF"));
    }

    // Виджет переводится на СОБСТВЕННЫЙ канвас, чтобы: 1) переживать смену сцен
    // (DontDestroyOnLoad применяется к корню — к этому канвасу), 2) не тащить за
    // собой весь сценный канвас (UserInterface) в DontDestroyOnLoad, 3) UI всегда
    // кликабелен (есть Canvas + GraphicRaycaster). Масштаб повторяет основной
    // канвас игры (ScaleWithScreenSize 800x480, match width).
    private void EnsureOwnCanvas()
    {
        Canvas parentCanvas = GetComponentInParent<Canvas>();
        if (parentCanvas != null && parentCanvas.transform.root == transform)
            return;
        GameObject canvasGO = new GameObject("MusicPlayerCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas cv = canvasGO.GetComponent<Canvas>();
        cv.renderMode = RenderMode.ScreenSpaceOverlay;
        cv.sortingOrder = 3000;
        CanvasScaler scaler = canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(800f, 480f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0f;
        transform.SetParent(canvasGO.transform, false);
        Debug.Log("[MusicPlayer]: Moved under own persistent canvas");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        PhotonNetwork.OnEventCall -= OnPhotonEvent;
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused && _audio != null && _audio.isPlaying)
        {
            _wasPlayingBeforePause = true;
            _audio.Pause();
            Debug.Log("[MusicPlayer]: Paused (app lost focus)");
        }
        else if (!paused && _audio != null && _wasPlayingBeforePause && _musicEnabled)
        {
            _wasPlayingBeforePause = false;
            _audio.Play();
            Debug.Log("[MusicPlayer]: Resumed (app focus back)");
        }
    }

    private void Update()
    {
        if (_audio != null && _audio.clip != null)
        {
            if (!_seeking && seekSlider != null)
            {
                seekSlider.maxValue = _audio.clip.length;
                seekSlider.value = _audio.time;
            }
            float now = Time.unscaledTime;
            if (now - _lastTimeLabelUpdate > 0.25f)
            {
                _lastTimeLabelUpdate = now;
                if (timeText != null)
                    timeText.text = FormatTime(_audio.time) + " / " + FormatTime(_audio.clip.length);
            }
        }

        // Auto-next
        if (_musicEnabled && _started && !_pausedByUser && _audio != null && _audio.clip != null
            && !_audio.isPlaying && _tracks.Count > 0)
        {
            Debug.Log("[MusicPlayer]: Track ended, auto next");
            NextTrack();
        }

        // Затухание фоновой музыки меню: отслеживаем смену состояния
        // "играет/не играет" и запускаем fade в нужную сторону.
        bool playingNow = _audio != null && _audio.clip != null && _audio.isPlaying && _musicEnabled;
        if (playingNow != _lastPlayingState)
        {
            _lastPlayingState = playingNow;
            UpdateMenuMusicFade();
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        HandleKeyboard();
#endif
    }

    // ============================================================
    // UI Setup
    // ============================================================
    private void SetupUI()
    {
        // Сначала чистим старые привязки из сцены (виджет мог быть настроен под
        // старую версию скрипта - там свои onClick), чтобы они не конфликтовали.
        if (playPauseButton != null) playPauseButton.onClick.RemoveAllListeners();
        if (nextButton != null) nextButton.onClick.RemoveAllListeners();
        if (prevButton != null) prevButton.onClick.RemoveAllListeners();
        if (collapseButton != null) collapseButton.onClick.RemoveAllListeners();
        if (disableButton != null) disableButton.onClick.RemoveAllListeners();
        if (restoreButton != null) restoreButton.onClick.RemoveAllListeners();
        if (addTrackButton != null) addTrackButton.onClick.RemoveAllListeners();
        if (trackDropdown != null) trackDropdown.onValueChanged.RemoveAllListeners();
        if (seekSlider != null) seekSlider.onValueChanged.RemoveAllListeners();
        if (volumeSlider != null) volumeSlider.onValueChanged.RemoveAllListeners();
        if (musicToggle != null) musicToggle.onValueChanged.RemoveAllListeners();
        if (syncToggle != null) syncToggle.onValueChanged.RemoveAllListeners();

        // Play/Pause
        if (playPauseButton != null)
            playPauseButton.onClick.AddListener(TogglePlayPause);

        // Next/Prev
        if (nextButton != null)
            nextButton.onClick.AddListener(NextTrack);
        if (prevButton != null)
            prevButton.onClick.AddListener(PrevTrack);

        // Track dropdown
        if (trackDropdown != null)
            trackDropdown.onValueChanged.AddListener(OnTrackDropdownChanged);

        // Collapse
        if (collapseButton != null)
            collapseButton.onClick.AddListener(() => { _collapsed = true; RefreshUIState(); Debug.Log("[MusicPlayer]: Collapsed"); });

        // Disable
        if (disableButton != null)
            disableButton.onClick.AddListener(() => SetPlayerEnabled(false));

        // Restore: показать плеер обратно (включить + выйти из свёрнутого режима)
        if (restoreButton != null)
            restoreButton.onClick.AddListener(() => SetPlayerEnabled(true));

        // Add track (file picker)
        if (addTrackButton != null)
            addTrackButton.onClick.AddListener(OnAddTrackClicked);

        // Seek slider
        if (seekSlider != null)
        {
            seekSlider.onValueChanged.AddListener(OnSeekValue);
        }

        // Volume slider
        if (volumeSlider != null)
        {
            volumeSlider.minValue = 0;
            volumeSlider.maxValue = 100;
            volumeSlider.wholeNumbers = true;
            volumeSlider.value = _volumePct;
            volumeSlider.onValueChanged.AddListener(v => SetVolume(Mathf.RoundToInt(v)));
        }

        // Music toggle
        if (musicToggle != null)
        {
            musicToggle.isOn = _musicEnabled;
            musicToggle.onValueChanged.AddListener(SetMusicEnabled);
        }

        // Sync toggle
        if (syncToggle != null)
        {
            syncToggle.isOn = _syncEnabled;
            syncToggle.onValueChanged.AddListener(SetSyncEnabled);
        }

        // Обновить тексты
        UpdateTitle();
        UpdateVolumeText();
    }

    // ============================================================
    // Public API
    // ============================================================
    public void PlayMusic() { TogglePlayPause(); }

    public void NextTrack()
    {
        if (_tracks.Count == 0) { Debug.Log("[MusicPlayer]: Next skipped (empty playlist)"); return; }
        int idx = _currentTrackIndex < 0 ? 0 : (_currentTrackIndex + 1) % _tracks.Count;
        PlayTrack(idx, 0f);
        SendSync("next");
    }

    public void PrevTrack()
    {
        if (_tracks.Count == 0) { Debug.Log("[MusicPlayer]: Prev skipped (empty playlist)"); return; }
        int idx = _currentTrackIndex < 0 ? _tracks.Count - 1 : (_currentTrackIndex - 1 + _tracks.Count) % _tracks.Count;
        PlayTrack(idx, 0f);
        SendSync("prev");
    }

    public void TogglePlayPause()
    {
        if (_audio == null) return;
        if (_audio.clip == null)
        {
            if (_tracks.Count == 0) { Debug.Log("[MusicPlayer]: Play skipped (empty playlist)"); return; }
            PlayTrack(0, 0f);
            return;
        }
        if (_audio.isPlaying)
        {
            _audio.Pause();
            _pausedByUser = true;
            Debug.Log("[MusicPlayer]: Paused");
            UpdatePlayButton();
            SendSync("pause");
        }
        else
        {
            if (!_musicEnabled)
            {
                Debug.Log("[MusicPlayer]: Resume blocked (music toggle OFF)");
                return;
            }
            _audio.Play();
            _pausedByUser = false;
            Debug.Log("[MusicPlayer]: Resumed");
            UpdatePlayButton();
            SendSync("play");
        }
    }

    // ============================================================
    // Remote mute: автономное глушение музыки у других игроков.
    // Команда уходит всем остальным в комнате сразу (без проверки
    // тумблера Sync) - как аналог старого "бана", но без списков.
    // ============================================================
    /// Разовая команда всем остальным игрокам: остановить музыку.
    public void MuteOthersMusic()
    {
        BroadcastMuteCommand("mute");
    }

    /// Разовая команда всем остальным игрокам: возобновить музыку.
    public void UnmuteOthersMusic()
    {
        BroadcastMuteCommand("unmute");
    }

    public bool LoadMusic(string resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath)) return false;
        AudioClip c = Resources.Load<AudioClip>(resourcePath);
        if (c == null)
        {
            Debug.Log("[MusicPlayer]: LoadMusic FAILED: \"" + resourcePath + "\" not found in Resources");
            return false;
        }
        if (!_tracks.Exists(t => t.id == c.name))
        {
            _tracks.Add(new TrackInfo(c.name, c));
            RefreshTrackDropdown();
        }
        Debug.Log("[MusicPlayer]: Manual load \"" + resourcePath + "\" -> track \"" + c.name + "\" (playlist: " + _tracks.Count + ")");
        return true;
    }

    public void SetVolume(int pct)
    {
        _volumePct = Mathf.Clamp(pct, 0, 100);
        if (_audio != null) _audio.volume = _volumePct / 100f;
        UpdateVolumeText();
        if (volumeSlider != null) volumeSlider.value = _volumePct;
        PlayerPrefs.SetInt(KEY_VOL, _volumePct);
        PlayerPrefs.Save();
        Debug.Log("[MusicPlayer]: Volume -> " + _volumePct + "%");
    }

    public void SetMusicEnabled(bool on)
    {
        _musicEnabled = on;
        PlayerPrefs.SetInt(KEY_MUSIC, on ? 1 : 0);
        PlayerPrefs.Save();
        if (!on)
        {
            if (_audio != null && _audio.isPlaying)
            {
                _audio.Pause();
                _pausedByUser = true;
            }
            Debug.Log("[MusicPlayer]: Music toggle OFF - this player will not hear music");
        }
        else
        {
            if (_audio != null && _audio.clip != null && _pausedByUser && _started)
            {
                _audio.Play();
                _pausedByUser = false;
                Debug.Log("[MusicPlayer]: Music toggle ON - resumed \"" + _currentTrackId + "\"");
            }
            else if (_audio != null && _audio.clip == null && _tracks.Count > 0)
            {
                PlayTrack(0, 0f);
            }
        }
        UpdatePlayButton();
    }

    public void SetSyncEnabled(bool on)
    {
        _syncEnabled = on;
        PlayerPrefs.SetInt(KEY_SYNC, on ? 1 : 0);
        PlayerPrefs.Save();
        Debug.Log("[MusicPlayer]: Sync " + (on ? "ON - play events broadcast to all players" : "OFF - local only"));
    }

    public void SetPlayerEnabled(bool on)
    {
        _playerEnabled = on;
        if (on) _collapsed = false;
        PlayerPrefs.SetInt(KEY_ENABLED, on ? 1 : 0);
        PlayerPrefs.Save();
        Debug.Log("[MusicPlayer]: Player " + (on ? "enabled" : "disabled"));
        RefreshUIState();
    }

    // ============================================================
    // Playback internals
    // ============================================================
    private void PlayTrack(int index, float fromTime)
    {
        if (_tracks.Count == 0) return;
        _currentTrackIndex = ((index % _tracks.Count) + _tracks.Count) % _tracks.Count;
        TrackInfo t = _tracks[_currentTrackIndex];
        _currentTrackId = t.id;
        _audio.clip = t.clip;
        _audio.time = Mathf.Clamp(fromTime, 0f, Mathf.Max(0f, t.clip.length - 0.05f));
        if (seekSlider != null) seekSlider.maxValue = t.clip.length;

        if (_musicEnabled)
        {
            _audio.Play();
            _started = true;
            _pausedByUser = false;
            Debug.Log("[MusicPlayer]: Play \"" + t.id + "\" (" + t.clip.length.ToString("F1") + "s) from " + fromTime.ToString("F1") + "s");
        }
        else
        {
            _pausedByUser = true;
            Debug.Log("[MusicPlayer]: Track \"" + t.id + "\" set but music toggle OFF - muted");
        }
        UpdateTitle();
        UpdatePlayButton();
        RefreshTrackDropdown();
    }

    private void ApplySeek(float seconds)
    {
        if (_audio == null || _audio.clip == null) return;
        _audio.time = Mathf.Clamp(seconds, 0f, _audio.clip.length);
        Debug.Log("[MusicPlayer]: Seek -> " + _audio.time.ToString("F1") + "s");
        SendSync("seek");
    }

    private void ApplyVolume()
    {
        if (_audio != null) _audio.volume = _volumePct / 100f;
        UpdateVolumeText();
        if (volumeSlider != null) volumeSlider.value = _volumePct;
    }

    // ============================================================
    // Playlist
    // ============================================================
    private void LoadPlaylist()
    {
        _tracks.Clear();
        if (defaultMusic != null)
        {
            foreach (AudioClip c in defaultMusic)
            {
                if (c == null) continue;
                if (!_tracks.Exists(t => t.id == c.name)) _tracks.Add(new TrackInfo(c.name, c));
            }
        }
        AudioClip[] all = Resources.LoadAll<AudioClip>("Music");
        if (all != null)
        {
            foreach (AudioClip c in all)
            {
                if (c == null) continue;
                if (!_tracks.Exists(t => t.id == c.name)) _tracks.Add(new TrackInfo(c.name, c));
            }
        }
        Debug.Log("[MusicPlayer]: Playlist loaded: " + _tracks.Count + " track(s)");
        RefreshTrackDropdown();
    }

    // ============================================================
    // Sync (Photon RaiseEvent)
    // ============================================================
    private void SendSync(string action)
    {
        if (!_syncEnabled) return;
        if (!PhotonNetwork.connected || PhotonNetwork.offlineMode || !PhotonNetwork.inRoom)
        {
            Debug.Log("[MusicPlayer]: Sync \"" + action + "\" skipped (not in room)");
            return;
        }
        float pos = _audio != null && _audio.clip != null ? _audio.time : 0f;
        object[] data = new object[] { _currentTrackId ?? "", pos, _audio == null || !_audio.isPlaying, _volumePct, action, SenderName() };
        bool ok = PhotonNetwork.RaiseEvent(MUSIC_EVENT_CODE, data, true, _othersOpts);
        Debug.Log("[MusicPlayer]: Sync sent action=" + action + " id=\"" + _currentTrackId + "\" pos=" + pos.ToString("F1") + " vol=" + _volumePct + "% ok=" + ok);
    }

    private void BroadcastMuteCommand(string action)
    {
        if (!PhotonNetwork.connected || PhotonNetwork.offlineMode || !PhotonNetwork.inRoom)
        {
            Debug.Log("[MusicPlayer]: Mute command \"" + action + "\" not broadcast (not in room)");
            return;
        }
        object[] data = new object[] { "", 0f, true, _volumePct, action, SenderName() };
        bool ok = PhotonNetwork.RaiseEvent(MUSIC_EVENT_CODE, data, true, _othersOpts);
        Debug.Log("[MusicPlayer]: Mute command \"" + action + "\" broadcast ok=" + ok);
    }

    private void OnPhotonEvent(byte eventCode, object content, int senderId)
    {
        if (eventCode != MUSIC_EVENT_CODE) return;
        object[] d = content as object[];
        if (d == null || d.Length < 6)
        {
            Debug.Log("[MusicPlayer]: Sync event ignored (bad payload)");
            return;
        }
        try
        {
            string id = (string)d[0];
            string action = (string)d[4];
            string sender = d[5] as string;
            if (string.IsNullOrEmpty(sender)) sender = "player#" + senderId;

            if (action == "mute")
            {
                if (_audio != null && _audio.clip != null && _audio.isPlaying)
                {
                    _audio.Pause();
                    _pausedByUser = true;
                    UpdatePlayButton();
                }
                Debug.Log("[MusicPlayer]: Music muted by \"" + sender + "\"");
                return;
            }

            if (action == "unmute")
            {
                if (_audio != null && _audio.clip != null && _musicEnabled && _pausedByUser && _started)
                {
                    _audio.Play();
                    _pausedByUser = false;
                    UpdatePlayButton();
                }
                Debug.Log("[MusicPlayer]: Music unmuted by \"" + sender + "\"");
                return;
            }

            if (!_syncEnabled)
            {
                Debug.Log("[MusicPlayer]: Sync event \"" + action + "\" ignored (sync toggle OFF)");
                return;
            }

            float pos = (float)d[1];
            bool paused = (bool)d[2];
            int vol = (int)d[3];

            switch (action)
            {
                case "play":
                case "next":
                case "prev":
                    ReceivePlay(id, pos, vol, sender);
                    break;
                case "pause":
                    if (_audio != null && _audio.clip != null && _audio.clip.name == id && _audio.isPlaying)
                    {
                        _audio.Pause();
                        _pausedByUser = true;
                        UpdatePlayButton();
                    }
                    Debug.Log("[MusicPlayer]: Sync received from \"" + sender + "\": pause \"" + id + "\"");
                    break;
                case "seek":
                    if (_audio != null && _audio.clip != null && _audio.clip.name == id)
                    {
                        _audio.time = Mathf.Clamp(pos, 0f, _audio.clip.length);
                        Debug.Log("[MusicPlayer]: Sync received from \"" + sender + "\": seek \"" + id + "\" -> " + pos.ToString("F1") + "s");
                    }
                    break;
                default:
                    Debug.Log("[MusicPlayer]: Sync received unknown action \"" + action + "\"");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.Log("[MusicPlayer]: Sync event error: " + e.Message);
        }
    }

    private void ReceivePlay(string id, float pos, int vol, string sender)
    {
        TrackInfo track = _tracks.Find(t => t.id == id);
        if (track == null)
        {
            AudioClip c = Resources.Load<AudioClip>("Music/" + id);
            if (c == null)
            {
                Debug.Log("[MusicPlayer]: Sync \"" + id + "\" not found locally, ignored");
                return;
            }
            track = new TrackInfo(c.name, c);
            _tracks.Add(track);
            Debug.Log("[MusicPlayer]: Loaded synced track \"" + id + "\" on the fly");
        }
        _currentTrackId = track.id;
        for (int i = 0; i < _tracks.Count; i++) if (_tracks[i] == track) { _currentTrackIndex = i; break; }
        _audio.clip = track.clip;
        _audio.time = Mathf.Clamp(pos, 0f, Mathf.Max(0f, track.clip.length - 0.05f));
        SetVolume(vol);
        if (seekSlider != null) seekSlider.maxValue = track.clip.length;

        if (_musicEnabled)
        {
            _audio.Play();
            _started = true;
            _pausedByUser = false;
            Debug.Log("[MusicPlayer]: Sync received from \"" + sender + "\": play \"" + id + "\" at " + pos.ToString("F1") + "s vol=" + vol + "% -> playing for everyone");
        }
        else
        {
            _pausedByUser = true;
            Debug.Log("[MusicPlayer]: Sync received from \"" + sender + "\": play \"" + id + "\" but music toggle OFF - muted");
        }
        UpdateTitle();
        UpdatePlayButton();
        RefreshTrackDropdown();
    }

    private string SenderName()
    {
        string n = PhotonNetwork.playerName;
        return string.IsNullOrEmpty(n) ? "player#" + (PhotonNetwork.player != null ? PhotonNetwork.player.ID : 0) : n;
    }

    // ============================================================
    // Track Dropdown
    // ============================================================
    private void RefreshTrackDropdown()
    {
        if (trackDropdown == null) return;
        _refreshingDropdown = true;

        string current = _currentTrackId;
        trackDropdown.ClearOptions();
        List<string> options = new List<string>();
        int selected = 0;
        for (int i = 0; i < _tracks.Count; i++)
        {
            if (_tracks[i].id == current) selected = i;
            options.Add(_tracks[i].id);
        }
        trackDropdown.AddOptions(options);
        trackDropdown.value = selected;

        _refreshingDropdown = false;
    }

    private void OnTrackDropdownChanged(int index)
    {
        if (_refreshingDropdown) return;
        if (_tracks.Count == 0 || index < 0 || index >= _tracks.Count) return;
        PlayTrack(index, 0f);
        SendSync("play");
    }

    // ============================================================
    // Prefs
    // ============================================================
    private void LoadPrefs()
    {
        _volumePct = Mathf.Clamp(PlayerPrefs.GetInt(KEY_VOL, 70), 0, 100);
        _musicEnabled = PlayerPrefs.GetInt(KEY_MUSIC, 1) == 1;
        _syncEnabled = PlayerPrefs.GetInt(KEY_SYNC, 0) == 1;
        _playerEnabled = PlayerPrefs.GetInt(KEY_ENABLED, 1) == 1;
    }

    // ============================================================
    // Add Track (file picker)
    // Editor/Windows: системный проводник. Android: список аудио
    // устройства (MediaStore, без плагинов). Файл копируется в
    // persistentDataPath/Music и загружается в плейлист.
    // ============================================================
    public void OnAddTrackClicked()
    {
#if UNITY_EDITOR
        string path = EditorUtility.OpenFilePanel("Select music track", "", "mp3,ogg,wav");
        if (!string.IsNullOrEmpty(path)) ImportTrackFromPath(path);
#elif UNITY_STANDALONE_WIN
        string path = ShowOpenFileDialog();
        if (!string.IsNullOrEmpty(path)) ImportTrackFromPath(path);
#elif UNITY_ANDROID
        ShowAndroidAudioPicker();
#else
        Debug.Log("[MusicPlayer]: Add track is not supported on this platform");
#endif
    }

    public void ImportTrackFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        StartCoroutine(ImportTrackCoroutine(path));
    }

    private IEnumerator ImportTrackCoroutine(string path)
    {
        string dir = Path.Combine(Application.persistentDataPath, "Music");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string fileName = Path.GetFileName(path);
        string dest = Path.Combine(dir, fileName);
        try
        {
            if (!File.Exists(dest) || !Path.GetFullPath(path).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(path, dest, true);
        }
        catch (Exception e)
        {
            Debug.Log("[MusicPlayer]: Import copy failed \"" + path + "\": " + e.Message);
            yield break;
        }
        yield return LoadClipFromFile(dest, Path.GetFileNameWithoutExtension(fileName), clip =>
        {
            if (clip == null) return;
            if (!_addedFiles.Contains(fileName)) { _addedFiles.Add(fileName); SaveAdded(); }
            AddTrack(clip, true);
        });
    }

    private IEnumerator LoadClipFromFile(string filePath, string trackId, Action<AudioClip> onDone)
    {
        string url = "file:///" + filePath.Replace('\\', '/');
        AudioType type = GetAudioTypeForPath(filePath);
        using (UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(url, type))
        {
            yield return uwr.SendWebRequest();
            if (uwr.result != UnityWebRequest.Result.Success)
            {
                Debug.Log("[MusicPlayer]: Load failed \"" + filePath + "\": " + uwr.error);
                onDone(null);
            }
            else
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(uwr);
                clip.name = trackId;
                onDone(clip);
            }
        }
    }

    private static AudioType GetAudioTypeForPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".mp3": return AudioType.MPEG;
            case ".ogg": return AudioType.OGGVORBIS;
            case ".wav": return AudioType.WAV;
            case ".aac":
            case ".m4a": return AudioType.ACC;
            default: return AudioType.UNKNOWN;
        }
    }

    private void AddTrack(AudioClip clip, bool playNow)
    {
        if (clip == null) return;
        if (_tracks.Exists(t => t.id == clip.name))
        {
            Debug.Log("[MusicPlayer]: Track \"" + clip.name + "\" already in playlist");
            return;
        }
        _tracks.Add(new TrackInfo(clip.name, clip));
        RefreshTrackDropdown();
        Debug.Log("[MusicPlayer]: Added track \"" + clip.name + "\" (playlist: " + _tracks.Count + ")");
        if (playNow)
        {
            PlayTrack(_tracks.Count - 1, 0f);
            SendSync("play");
        }
    }

    private IEnumerator LoadAddedTracks()
    {
        string list = PlayerPrefs.GetString(KEY_ADDED, "");
        if (string.IsNullOrEmpty(list)) yield break;
        foreach (string name in list.Split(','))
        {
            string f = name.Trim();
            if (f.Length == 0) continue;
            _addedFiles.Add(f);
            string id = Path.GetFileNameWithoutExtension(f);
            string path = Path.Combine(Application.persistentDataPath, "Music", f);
            if (!File.Exists(path))
            {
                Debug.Log("[MusicPlayer]: Added track file missing: \"" + path + "\"");
                continue;
            }
            yield return LoadClipFromFile(path, id, clip => { if (clip != null) AddTrack(clip, false); });
        }
    }

    private void SaveAdded()
    {
        PlayerPrefs.SetString(KEY_ADDED, string.Join(",", _addedFiles.ToArray()));
        PlayerPrefs.Save();
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFile;
        public int nMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    private static string ShowOpenFileDialog()
    {
        OpenFileName ofn = new OpenFileName();
        ofn.lStructSize = Marshal.SizeOf(typeof(OpenFileName));
        ofn.lpstrFilter = "Audio files\0*.mp3;*.ogg;*.wav\0All files\0*.*\0";
        ofn.lpstrFile = new string('\0', 260);
        ofn.nMaxFile = 260;
        ofn.lpstrFileTitle = new string('\0', 260);
        ofn.nMaxFileTitle = 260;
        ofn.Flags = 0x00000008 | 0x00001000 | 0x00000004; // OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY
        if (GetOpenFileNameW(ref ofn))
        {
            int end = ofn.lpstrFile.IndexOf('\0');
            return end >= 0 ? ofn.lpstrFile.Substring(0, end) : ofn.lpstrFile;
        }
        return null;
    }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _androidActivity;
    private AndroidJavaObject _androidResolver;
    private GameObject _pickerRoot;

    private class AndroidTrackEntry
    {
        public string id;
        public string title;
        public string displayName;
        public string duration;
    }

    private readonly List<AndroidTrackEntry> _pickerTracks = new List<AndroidTrackEntry>();

    private AndroidJavaObject GetAndroidActivity()
    {
        if (_androidActivity == null)
        {
            AndroidJavaClass jc = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            _androidActivity = jc.GetStatic<AndroidJavaObject>("currentActivity");
        }
        return _androidActivity;
    }

    private void ShowAndroidAudioPicker()
    {
        try
        {
            _pickerTracks.Clear();
            AndroidJavaObject activity = GetAndroidActivity();
            _androidResolver = activity.Call<AndroidJavaObject>("getContentResolver");
            AndroidJavaObject baseUri = new AndroidJavaClass("android.net.Uri").CallStatic<AndroidJavaObject>("parse", "content://media/external/audio/media");
            AndroidJavaObject cursor = _androidResolver.Call<AndroidJavaObject>("query", baseUri, null, null, null, "title ASC");
            if (cursor == null)
            {
                Debug.Log("[MusicPlayer]: Android audio query returned null (no media store access)");
                return;
            }
            try
            {
                int idIdx = cursor.Call<int>("getColumnIndex", "_id");
                int titleIdx = cursor.Call<int>("getColumnIndex", "title");
                int displayIdx = cursor.Call<int>("getColumnIndex", "_display_name");
                int durationIdx = cursor.Call<int>("getColumnIndex", "duration");
                while (cursor.Call<bool>("moveToNext"))
                {
                    AndroidTrackEntry e = new AndroidTrackEntry();
                    e.id = idIdx >= 0 ? cursor.Call<string>("getString", idIdx) : null;
                    e.title = titleIdx >= 0 ? cursor.Call<string>("getString", titleIdx) : null;
                    e.displayName = displayIdx >= 0 ? cursor.Call<string>("getString", displayIdx) : null;
                    e.duration = durationIdx >= 0 ? cursor.Call<string>("getString", durationIdx) : null;
                    if (string.IsNullOrEmpty(e.id)) continue;
                    if (string.IsNullOrEmpty(e.title)) e.title = e.displayName ?? e.id;
                    _pickerTracks.Add(e);
                }
            }
            finally
            {
                cursor.Call("close");
            }
            Debug.Log("[MusicPlayer]: Android audio picker found " + _pickerTracks.Count + " track(s)");
            if (_pickerTracks.Count == 0)
            {
                Debug.Log("[MusicPlayer]: No audio files found on the device");
                return;
            }
            BuildAndroidPickerUI();
        }
        catch (Exception e)
        {
            Debug.Log("[MusicPlayer]: Android audio picker failed: " + e.Message);
        }
    }

    private void BuildAndroidPickerUI()
    {
        CloseAndroidPickerUI();
        GameObject root = new GameObject("MusicPickerCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas cv = root.GetComponent<Canvas>();
        cv.renderMode = RenderMode.ScreenSpaceOverlay;
        cv.sortingOrder = 30000;
        _pickerRoot = root;

        GameObject bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(root.transform, false);
        bg.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.65f);
        RectTransform bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        GameObject panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(root.transform, false);
        RectTransform pRt = panel.GetComponent<RectTransform>();
        pRt.anchorMin = new Vector2(0.5f, 0.5f);
        pRt.anchorMax = new Vector2(0.5f, 0.5f);
        pRt.sizeDelta = new Vector2(Screen.width * 0.85f, Screen.height * 0.7f);
        panel.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.15f, 0.97f);

        GameObject title = new GameObject("Title", typeof(RectTransform), typeof(Text));
        title.transform.SetParent(panel.transform, false);
        Text t = title.GetComponent<Text>();
        t.text = "Выберите трек (" + _pickerTracks.Count + ")";
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 22;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        RectTransform tRt = title.GetComponent<RectTransform>();
        tRt.anchorMin = new Vector2(0f, 1f);
        tRt.anchorMax = new Vector2(1f, 1f);
        tRt.sizeDelta = new Vector2(0f, 40f);
        tRt.anchoredPosition = new Vector2(0f, -2f);

        GameObject cancel = CreatePickerButton("Cancel", "Отмена", panel, CloseAndroidPickerUI);
        RectTransform cRt = cancel.GetComponent<RectTransform>();
        cRt.anchorMin = new Vector2(1f, 0f);
        cRt.anchorMax = new Vector2(1f, 0f);
        cRt.sizeDelta = new Vector2(160f, 44f);
        cRt.anchoredPosition = new Vector2(-10f, 8f);
        cancel.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;

        GameObject scrollGO = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        scrollGO.transform.SetParent(panel.transform, false);
        RectTransform sRt = scrollGO.GetComponent<RectTransform>();
        sRt.anchorMin = new Vector2(0f, 0f);
        sRt.anchorMax = new Vector2(1f, 1f);
        sRt.offsetMin = new Vector2(10f, 58f);
        sRt.offsetMax = new Vector2(-10f, -46f);
        scrollGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);

        GameObject viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
        viewport.transform.SetParent(scrollGO.transform, false);
        RectTransform vRt = viewport.GetComponent<RectTransform>();
        vRt.anchorMin = Vector2.zero;
        vRt.anchorMax = Vector2.one;
        vRt.offsetMin = Vector2.zero;
        vRt.offsetMax = Vector2.zero;
        Image vImg = viewport.GetComponent<Image>();
        vImg.color = Color.white;
        vImg.raycastTarget = false;
        viewport.GetComponent<Mask>().showMaskGraphic = false;

        GameObject content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        content.transform.SetParent(viewport.transform, false);
        RectTransform cRt2 = content.GetComponent<RectTransform>();
        cRt2.anchorMin = new Vector2(0f, 1f);
        cRt2.anchorMax = new Vector2(1f, 1f);
        cRt2.pivot = new Vector2(0.5f, 1f);
        cRt2.sizeDelta = new Vector2(0f, 0f);
        VerticalLayoutGroup vlg = content.GetComponent<VerticalLayoutGroup>();
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.spacing = 4f;
        vlg.padding = new RectOffset(4, 4, 4, 4);
        content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ScrollRect sr = scrollGO.GetComponent<ScrollRect>();
        sr.content = cRt2;
        sr.viewport = vRt;
        sr.horizontal = false;
        sr.vertical = true;

        foreach (AndroidTrackEntry e in _pickerTracks)
        {
            string label = e.title;
            long durMs;
            if (long.TryParse(e.duration, out durMs) && durMs > 0)
                label += "  [" + FormatTime(durMs / 1000f) + "]";
            AndroidTrackEntry entry = e;
            CreatePickerButton("Track_" + e.id, label, content, () => CopyPickerTrack(entry));
        }
    }

    private static GameObject CreatePickerButton(string name, string label, Transform parent, UnityAction onClick)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Button), typeof(Image));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 42f);
        Button b = go.GetComponent<Button>();
        Image img = go.GetComponent<Image>();
        img.color = new Color(0.22f, 0.22f, 0.28f);
        b.targetGraphic = img;
        ColorBlock colors = b.colors;
        colors.normalColor = new Color(0.22f, 0.22f, 0.28f);
        colors.highlightedColor = new Color(0.32f, 0.32f, 0.4f);
        colors.pressedColor = new Color(0.12f, 0.12f, 0.16f);
        b.colors = colors;
        b.onClick.AddListener(onClick);
        Text txt = go.AddComponent<Text>();
        txt.text = label;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.fontSize = 16;
        txt.color = Color.white;
        txt.alignment = TextAnchor.MiddleLeft;
        txt.raycastTarget = false;
        return go;
    }

    private void CopyPickerTrack(AndroidTrackEntry entry)
    {
        try
        {
            AndroidJavaObject baseUri = new AndroidJavaClass("android.net.Uri").CallStatic<AndroidJavaObject>("parse", "content://media/external/audio/media");
            AndroidJavaObject uri = new AndroidJavaClass("android.net.Uri").CallStatic<AndroidJavaObject>("withAppendedPath", baseUri, entry.id);
            AndroidJavaObject input = _androidResolver.Call<AndroidJavaObject>("openInputStream", uri);
            if (input == null)
            {
                Debug.Log("[MusicPlayer]: Cannot open \"" + entry.title + "\"");
                return;
            }
            string dir = Path.Combine(Application.persistentDataPath, "Music");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string fileName = SanitizeFileName(entry.displayName ?? entry.title + ".mp3");
            if (string.IsNullOrEmpty(Path.GetExtension(fileName))) fileName += ".mp3";
            string dest = Path.Combine(dir, fileName);
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] chunk = new byte[65536];
                using (AndroidJavaObject jarr = new AndroidJavaObject("[B", chunk.Length))
                {
                    int n;
                    while ((n = input.Call<int>("read", jarr, 0, chunk.Length)) > 0)
                    {
                        AndroidJNI.GetByteArrayRegion(jarr.GetRawObject(), 0, n, chunk);
                        ms.Write(chunk, 0, n);
                    }
                }
                input.Call("close");
                File.WriteAllBytes(dest, ms.ToArray());
            }
            CloseAndroidPickerUI();
            Debug.Log("[MusicPlayer]: Copied \"" + fileName + "\" (" + new FileInfo(dest).Length + " bytes)");
            if (!_addedFiles.Contains(fileName)) { _addedFiles.Add(fileName); SaveAdded(); }
            StartCoroutine(LoadClipFromFile(dest, Path.GetFileNameWithoutExtension(fileName), clip => { if (clip != null) AddTrack(clip, true); }));
        }
        catch (Exception e)
        {
            Debug.Log("[MusicPlayer]: Android copy failed: " + e.Message);
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "track.mp3";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private void CloseAndroidPickerUI()
    {
        if (_pickerRoot != null) Destroy(_pickerRoot);
        _pickerRoot = null;
    }
#endif

    // ============================================================
    // Menu music fade
    // ============================================================
    private void UpdateMenuMusicFade()
    {
        if (menuMusicSource == null) return;
        if (_menuMusicFade != null) StopCoroutine(_menuMusicFade);
        bool playing = _audio != null && _audio.isPlaying && _musicEnabled;
        _menuMusicFade = StartCoroutine(FadeMenuMusic(playing ? 0f : _menuMusicBaseVolume));
    }

    private IEnumerator FadeMenuMusic(float targetVolume)
    {
        if (menuMusicSource == null) yield break;
        if (_menuMusicBaseVolume < 0f) _menuMusicBaseVolume = menuMusicSource.volume;
        float start = menuMusicSource.volume;
        float t = 0f;
        while (t < menuMusicFadeSeconds)
        {
            t += Time.unscaledDeltaTime;
            menuMusicSource.volume = Mathf.Lerp(start, targetVolume, Mathf.Clamp01(t / menuMusicFadeSeconds));
            yield return null;
        }
        menuMusicSource.volume = targetVolume;
        _menuMusicFade = null;
    }

    // ============================================================
    // UI Update Helpers
    // ============================================================
    private void RefreshUIState()
    {
        if (mainPanel != null) mainPanel.SetActive(_playerEnabled && !_collapsed);
        if (collapsedRestore != null) collapsedRestore.SetActive(_playerEnabled && _collapsed);
        if (disabledPanel != null) disabledPanel.SetActive(!_playerEnabled);
    }

    private void UpdateTitle()
    {
        if (titleText != null)
            titleText.text = _currentTrackId != null ? "♪ " + _currentTrackId : "♪ —";
    }

    private void UpdateVolumeText()
    {
        if (volumeText != null)
            volumeText.text = _volumePct + "%";
    }

    private void UpdatePlayButton()
    {
        if (playPauseButton == null) return;
        bool playing = _audio != null && _audio.isPlaying;
        Image img = playPauseButton.GetComponent<Image>();
        Text btnText = playPauseButton.GetComponentInChildren<Text>();
        if (img != null && playingIconSprite != null && pausedIconSprite != null)
        {
            img.sprite = playing ? pausedIconSprite : playingIconSprite;
            if (btnText != null) btnText.text = "";
        }
        else if (btnText != null)
        {
            btnText.text = playing ? "⏸" : "▶";
        }
    }

    private void OnSeekValue(float v)
    {
        if (_audio != null && _audio.clip != null && Mathf.Abs(_audio.time - v) > 0.05f)
        {
            ApplySeek(v);
        }
    }

    private static string FormatTime(float t)
    {
        if (t < 0f) t = 0f;
        int m = (int)(t / 60f);
        int s = (int)(t % 60f);
        return m + ":" + (s < 10 ? "0" + s : s.ToString());
    }

    // ============================================================
    // Keyboard (Editor/Standalone)
    // ============================================================
#if UNITY_EDITOR || UNITY_STANDALONE
    private void HandleKeyboard()
    {
        if (mainPanel == null || !mainPanel.activeSelf) return;
        if (Input.GetKeyDown(KeyCode.Space)) TogglePlayPause();
        else if (Input.GetKeyDown(KeyCode.LeftArrow)) PrevTrack();
        else if (Input.GetKeyDown(KeyCode.RightArrow)) NextTrack();
        else if (Input.GetKeyDown(KeyCode.M)) { SetMusicEnabled(!_musicEnabled); if (musicToggle != null) musicToggle.isOn = _musicEnabled; }
        else if (Input.GetKeyDown(KeyCode.S)) { SetSyncEnabled(!_syncEnabled); if (syncToggle != null) syncToggle.isOn = _syncEnabled; }
        else if (Input.GetKeyDown(KeyCode.RightBracket)) SetVolume(_volumePct + 5);
        else if (Input.GetKeyDown(KeyCode.LeftBracket)) SetVolume(_volumePct - 5);
    }
#endif
}
