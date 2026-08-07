# Music Player for Unity with Photon
A lightweight, UI-independent music player for Unity with Photon multiplayer support. The player can be embedded into any existing UI with your own styling.

### Features
- UI-independent - assign any UI elements from your existing interface
- Multi-scene persistence - works across all scenes with DontDestroyOnLoad
- Photon synchronization - sync play/pause/seek/next/prev across all players in a room
- File browser integration - add music files from device:
- Windows: native file dialog
- Android: MediaStore audio picker
- Editor: Unity file picker
- Persistent storage - added tracks stored in Application.persistentDataPath/Music
- Playlist management - dropdown for track selection, auto-advance on track end
- Collapse/Expand - minimize or fully disable the player UI
- Menu music fade - smoothly fade background menu music when player starts
### Requirements
- Unity 2019.4 or newer
- Photon Classic
- Unity UI package
## Installation / Setup
1. Copy the MusicPlayer.cs script into your project's Assets/Scripts/ folder
2. Ensure Photon is installed and configured in your project
3. Add the component to any GameObject in your scene (or use Tools → Music Player → Add To Current Scene)
4. Create Your UI **Required elements:**
```
- **Buttons:** Play/Pause, Next, Prev, Add Track (optional)
- **Texts:** Track title, Current time, Volume display
- **Sliders:** Seek progress, Volume control
- **Toggles:** Music on/off, Sync on/off
- **Dropdown:** Track selection
- **Panels:** Main panel, Collapsed state, Disabled state
```
5. Assign References
6. Add Music Files
- Option A - Resources Folder
  ```
  1. Create Assets/Resources/Music/ folder
  2. Drop any MP3/OGG/WAV files into it
  3. Files load automatically on startup
  ```
- Option B - Default Music Array
  ```
  1. Assign AudioClips directly in the Inspector's Default Music field
  ```
- Option C - Runtime via File Browser
  ```
  1. Click the "Add Track" button (if implemented)
  2. Select audio files from your device
  3. Files are copied to persistentDataPath/Music and loaded automatically
  ```
- Option D - Via Code
    ```csharp
    MusicPlayer.Instance.LoadMusic("Music/song_name");
    ```
## Public API
```csharp
// Playback control
MusicPlayer.Instance.PlayMusic();
MusicPlayer.Instance.NextTrack();
MusicPlayer.Instance.PrevTrack();
MusicPlayer.Instance.TogglePlayPause();

// Track management
MusicPlayer.Instance.LoadMusic("Music/track_name");
MusicPlayer.Instance.BanCurrentSong();
MusicPlayer.Instance.BanSongById("track_name");

// Settings
MusicPlayer.Instance.SetVolume(70);
MusicPlayer.Instance.SetMusicEnabled(true);
MusicPlayer.Instance.SetSyncEnabled(true);
MusicPlayer.Instance.SetPlayerEnabled(true);

// Query
MusicPlayer.Instance.GetKickListSummary();
MusicPlayer.Instance.GetKickCount();
```
## Photon Sync
When sync is enabled (toggle ON), all playback actions are broadcast to all players in the room:
```
- Play/Pause
- Track switching (Next/Prev)
- Seek position
```
**Event Code:** 199
**Payload:** [trackId, position, isPaused, volume, action, senderName]

## File Format Support
```
- MP3 (.mp3)
- OGG Vorbis (.ogg)
- WAV (.wav)
- AAC/M4A (.aac, .m4a) - Android only
```

## Keyboard Shortcuts (Editor/Standalone)
```
Space    - Play/Pause
Left     - Previous track
Right    - Next track
M        - Toggle music
S        - Toggle sync
K        - Show kick list
[        - Volume -5
]        - Volume +5
```
## Platform Support
| Platform | Add Track | Notes |
|----------|-----------|-------|
| Windows Editor | ✅ | Native file dialog |
| Windows Standalone | ✅ | Native file dialog |
| Android | ✅ | MediaStore integration |
| Other | ❌ | Files must be in Resources or assigned manually |

## Credits

- **Author**: vinzzzmoke
- **License**: Apache License 2.0

## License

This project is licensed under the Apache License 2.0 - see the [LICENSE](LICENSE) file for details.

Copyright 2026 vinzzzmoke

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
