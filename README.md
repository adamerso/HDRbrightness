**HDRbrightness** is a simple Windows application that resides in the system tray and allows you to:
- Adjust HDR screen brightness using global hotkeys (Win + PgUp and Win + PgDn),
- Enable/disable launching at system startup (toggle in the tray menu),
- Quickly restart the application (e.g., in case of issues) by pressing both hotkeys in quick succession or through the menu.  
  *(During my tests, I experienced a situation where the app stopped working correctly, so I implemented a reset mechanism right away.)*

## NOTE

**This application operates on HDR brightness settings — it will not work on devices without HDR support or if HDR is not enabled.**

## PURPOSE

**Originally, this application was intended solely as a workaround to adjust screen brightness on a laptop with a dedicated GPU (where, in Hybrid mode, brightness control wouldn't work). Over time, I adapted it for multiple monitors. I find it extremely useful when working with standard monitors where diving into the on-screen menu to change brightness takes so much effort that I typically never bothered...**

## Key Features

1. **Global Hotkeys**:  
   - Win + PgUp – Increases brightness by 0.5 (respecting the set range),  
   - Win + PgDn – Decreases brightness by 0.5 (respecting the set range),  
   - Pressing both hotkeys quickly in succession – automatically restarts the application.

2. **Tray Icon**:  
   - Right-click menu includes options for brightening/dimming, switching between normal/extended range, enabling startup with Windows, and exiting the program.

3. **Registry-Based Settings**:  
   - The application reads and writes the current brightness and range mode in `HKCU\Software\BrightnessTrayApp`.

4. **Command-Line Support**:  
   - No arguments – starts the application in tray mode,  
   - `/brighter`, `/darker`, `/set X`, `/debug-set X`, etc. – starts the application in console mode and adjusts brightness according to the specified parameters (see below).

For convenience, you can use the **Start with Windows** option in the tray menu (it adds startup from the current path).

## Usage (Command Line)

- `BrightnessTrayApp.exe /set {value}` – Sets brightness within the range 1.0–6.0 (if the value is outside this range, it will be automatically clamped).  
- `BrightnessTrayApp.exe /debug-set {value}` – Sets brightness to any value (ignoring limits).  
- `BrightnessTrayApp.exe /brighter` – Increases brightness by 0.5 (according to the current normal/extended mode stored in the registry).  
- `BrightnessTrayApp.exe /ext-brighter` – Increases brightness by 0.5, temporarily forcing the range up to 12.0 without changing the registry setting.  
- `BrightnessTrayApp.exe /darker` – Decreases brightness by 0.5 (according to the current mode).  

## Limitations / Notes

- **Undocumented API**: The program uses an ordinal call to a function in `dwmapi.dll` (ordinal 171: `DwmpSDRToHDRBoostPtr`). This method is unofficial/undocumented and may stop working in future Windows versions. This method was discovered and published by GitHub user BoatStuck: [https://github.com/BoatStuck/SDRBrightness/tree/main](https://github.com/BoatStuck/SDRBrightness/tree/main).  
- The application supports two ranges — normal/extended. The normal range corresponds to Windows’ default 1–6; the extended range stretches up to 12 (from my observations, you can achieve a brighter screen, though it comes at the expense of color accuracy).  
- Tested primarily on Windows Server 2022, so it should at least work on Windows 10. It may behave differently on other versions of Windows. Verified on a notebook display as well as external monitors in a multi-monitor environment — brightness is set for all screens simultaneously, equalizing their coefficient.  
- The current range and the last-set brightness are stored in the user's registry under `HKEY_CURRENT_USER\Software\BrightnessTrayApp`.  
- I haven’t found a method to read the current system brightness value directly. That’s why the app reads from the registry before setting brightness. As long as you don’t manually change brightness via the Windows slider, everything works correctly.  
- Greetings to GPT, without whom this wouldn’t exist :D

## Contributing

- Pull requests with fixes and suggestions are welcome.
- If you find a bug, feel free to open an issue in this repository.
