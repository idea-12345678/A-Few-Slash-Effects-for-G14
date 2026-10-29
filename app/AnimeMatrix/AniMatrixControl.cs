using GHelper.Helpers;
using GHelper.Mode;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Timers;

namespace GHelper.AnimeMatrix
{

    public enum MatrixMode
    {
        Banner = 0,
        Logo = 1,
        Picture = 2,
        Clock = 3,
        Audio = 4,
        Text = 5
    }

    public class AniMatrixControl
    {

        SettingsForm settings;

        System.Timers.Timer matrixTimer = default!;
        System.Timers.Timer slashTimer = default!;
        System.Timers.Timer slashStreamTimer = default!;
        int streamIndex = 0;
        int streamStepMs = 250;
        int fishTick = 0;
        int batteryTick = 0;
        uint showRng = 0x9E3779B9u;
        int sceneType = 0;
        int sceneTicks = 0;
        int sceneDur = 0;
        int sceneParam = 0;
        long sceneMask = 0;

        public AnimeMatrixDevice? deviceMatrix;
        public SlashDevice? deviceSlash;

        public static bool lidClose = false;

        public bool IsValid => deviceMatrix != null || deviceSlash != null;
        public bool IsSlash => deviceSlash != null;

        public bool IsGated => AppConfig.Get("matrix_brightness", 0) == 0
            || (AppConfig.Is("matrix_auto") && SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online)
            || (AppConfig.Is("matrix_lid") && lidClose);

        public static MatrixMode Mode => (MatrixMode)AppConfig.Get("matrix_running", 0);

        private long lastPresent;
        private List<double> maxes = new List<double>();

        private bool matrixSpectrogram = false;
        private long lastSpectro;
        private double[] spectroLevels = new double[20];
        private List<byte[]> spectroSlices = new List<byte[]>();
        
        private int slashBrightness = 0;
        private SlashMode slashMode;

        public AniMatrixControl(SettingsForm settingsForm)
        {
            settings = settingsForm;
            if (!AppConfig.IsSlash() && !AppConfig.IsAnimeMatrix()) return;
            
            try
            {
                if (AppConfig.IsSlash())
                {
                    deviceSlash = SlashDevice.Detect();
                }
                else
                {
                    deviceMatrix = new AnimeMatrixDevice();
                }

                matrixTimer = new System.Timers.Timer(100);
                matrixTimer.Elapsed += MatrixTimer_Elapsed;

            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }

        }

        bool disposed;

        public void SetDevice(bool wakeUp = false)
        {
            if (disposed) return;
            if (deviceMatrix is not null) SetMatrix(wakeUp);
            if (deviceSlash is not null) SetSlash(wakeUp);
        }

        public void SetSlash(bool wakeUp = false)
        {
            if (deviceSlash is null) return;

            int brightness = AppConfig.Get("matrix_brightness", 0);
            int running = AppConfig.Get("matrix_running", 0);
            int inteval = AppConfig.Get("matrix_interval", 0);

            StopAudio();
            StreamTimer_stop();

            Task.Run(() =>
            {
                try
                {
                    deviceSlash.SetProvider();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                    return;
                }

                if (wakeUp) deviceSlash.WakeUp();

                if (brightness == 0)
                {
                    deviceSlash.SetEnabled(false);
                }
                else
                {
                    deviceSlash.SetEnabled(true);

                    switch ((SlashMode)running)
                    {
                        case SlashMode.Static:
                            var custom = AppConfig.GetString("slash_custom");
                            if (custom is not null && custom.Length > 0)
                            {
                                Logger.WriteLine("Slash: Static");
                                deviceSlash.SetCustom(AppConfig.StringToBytes(custom));
                            }
                            else
                            {
                                deviceSlash.Init();
                                deviceSlash.SetMode((SlashMode)running);
                                deviceSlash.SetOptions(true, brightness, inteval);
                                deviceSlash.Save();
                            }
                            break;
                        case SlashMode.BatteryLevel:
                            // call tick to immediately update the pattern
                            Logger.WriteLine("Slash: Battery Level");
                            SlashTimer_start();
                            SlashTimer_tick();
                            break;
                        case SlashMode.Audio:
                        case SlashMode.AudioSpectrum:
                            slashMode = (SlashMode)running;
                            Logger.WriteLine("Slash: Audio");
                            SetAudio();
                            break;
                        case SlashMode.Stream01:
                        case SlashMode.Stream02:
                        case SlashMode.Fish01:
                        case SlashMode.BatteryShow:
                            Logger.WriteLine($"Slash: {(SlashMode)running} ({StreamStepFor((SlashMode)running)} ms, {LitSegmentsForCurrentMode()} LED)");
                            deviceSlash.Init();
                            deviceSlash.SetCustom(new byte[deviceSlash.LedCount]);
                            StreamTimer_start((SlashMode)running);
                            break;
                        case SlashMode.Dark:
                            deviceSlash.Init();
                            deviceSlash.SetFlag(0xAB, false);
                            break;
                        default:
                            deviceSlash.Init();
                            deviceSlash.SetMode((SlashMode)running);
                            deviceSlash.SetOptions(true, brightness, inteval);
                            deviceSlash.Save();
                            break;
                    }
                }
            });
        }

        public void SetLidMode(bool force = false)
        {
            if (deviceMatrix is not null && (AppConfig.Is("matrix_lid") || force))
            {
                Logger.WriteLine($"Matrix LidClosed: {lidClose}");
                SetDevice(true);
            }
        }

        public void SetBatteryAuto()
        {
            if (deviceMatrix is not null) SetMatrix();
        }

        public void SetMatrix(bool wakeUp = false)
        {

            if (deviceMatrix is null) return;

            int brightness = AppConfig.Get("matrix_brightness", 0);
            MatrixMode running = Mode;
            bool gated = IsGated;

            StopMatrixTimer();
            StopAudio();

            Task.Run(() =>
            {
                try
                {
                    deviceMatrix.SetProvider();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                    return;
                }

                if (wakeUp) deviceMatrix.WakeUp();

                if (gated)
                {
                    deviceMatrix.ClearFrames();
                    deviceMatrix.SetDisplayState(false);
                    deviceMatrix.SetDisplayState(false); // some devices are dumb
                    Logger.WriteLine("Matrix Off");

                    // editor open: keep rendering previews
                    if (deviceMatrix.OnPresent == null) return;
                }
                else
                {
                    if (wakeUp) deviceMatrix.WakeUp();
                    deviceMatrix.SetDisplayState(true);
                    deviceMatrix.SetBrightness((BrightnessMode)brightness);
                }

                switch (running)
                {
                    case MatrixMode.Picture:
                        SetMatrixPicture(AppConfig.GetString("matrix_picture"));
                        break;
                    case MatrixMode.Clock:
                        SetMatrixClock();
                        break;
                    case MatrixMode.Audio:
                        SetAudio();
                        break;
                    case MatrixMode.Text:
                        SetMatrixText();
                        break;
                    default:
                        SetBuiltIn((int)running);
                        break;
                }
            });


        }

        private void SetBuiltIn(int running)
        {
            BuiltInAnimation animation = new BuiltInAnimation(
                (BuiltInAnimation.Running)running,
                (BuiltInAnimation.Sleeping)AppConfig.Get("matrix_sleep", (int)BuiltInAnimation.Sleeping.Starfield),
                (BuiltInAnimation.Shutdown)AppConfig.Get("matrix_shutdown", (int)BuiltInAnimation.Shutdown.SeeYa),
                (BuiltInAnimation.Startup)AppConfig.Get("matrix_startup", (int)BuiltInAnimation.Startup.StaticEmergence)
            );
            deviceMatrix.ClearFrames();
            deviceMatrix.SetBuiltInAnimation(true, animation);
            Logger.WriteLine("Matrix builtin: " + animation.AsByte);
        }

        private void StartMatrixTimer(int interval = 100)
        {
            if (disposed) return;
            matrixTimer.Interval = interval;
            matrixTimer.Start();
        }

        public void StopMatrixTimer()
        {
            if (disposed) return;
            matrixTimer.Stop();
        }

        private void MatrixTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {

            if (deviceMatrix is null) return;

            // exception here kills the app
            try
            {
                switch (Mode)
                {
                    case MatrixMode.Picture:
                    case MatrixMode.Text:
                        deviceMatrix.PresentNextFrame();
                        break;
                    case MatrixMode.Clock:
                        deviceMatrix.PresentClock();
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }

        }

        public void SetMatrixClock()
        {
            StopAudio();

            try
            {
                deviceMatrix.ClearFrames();
                deviceMatrix.SetBuiltInAnimation(false);
                StartMatrixTimer(1000);
                Logger.WriteLine("Matrix Clock");
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }
        }

        public static readonly string[] TextFonts = { "", "Arial", "Segoe UI", "Consolas", "Impact" };

        public void SetMatrixText()
        {
            if (deviceMatrix is null) return;

            StopMatrixTimer();
            StopAudio();

            try
            {
                deviceMatrix.SetBuiltInAnimation(false);
                if (deviceMatrix.SetText()) StartMatrixTimer(100);
                Logger.WriteLine("Matrix Text");
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }
        }


        private void SlashTimer_start(int interval = 180000)
        {
            // 100% to 0% in 1hr = 1% every 36 seconds
            // 1 bracket every 14.2857 * 36s = 514s ~ 8m 30s
            // only ~5 actually distinguishable levels, so refresh every <= 514/5 ~ 100s
            // default is 60s

            // create the timer if first call
            // this way, the timer only spawns if user tries to use battery pattern
            if (slashTimer == default(System.Timers.Timer))
            {
                slashTimer = new System.Timers.Timer(interval);
                slashTimer.Elapsed += SlashTimer_elapsed;
                slashTimer.AutoReset = true;
            }
            // only write if interval changed
            if (slashTimer.Interval != interval)
            {
                slashTimer.Interval = interval;
            }

            slashTimer.Start();
        }

        private void SlashTimer_elapsed(object? sender, ElapsedEventArgs e)
        {
            try { SlashTimer_tick(); }
            catch (Exception ex) { Logger.WriteLine(ex.Message); }
        }

        private void SlashTimer_tick()
        {
            if (deviceSlash is null) return;

            //stop timer if called but not in battery pattern mode
            if ((SlashMode)AppConfig.Get("matrix_running", 0) != SlashMode.BatteryLevel)
            {
                slashTimer.Stop();
                return;
            }

            deviceSlash.SetBatteryPattern(AppConfig.Get("matrix_brightness", 0));
        }

        // Host-driven modes: Stream01 = 250 ms, Stream02 = 150 ms, Fish01 = 60 ms, BatteryShow = 100 ms.
        private static int StreamStepFor(SlashMode mode) => mode switch
        {
            SlashMode.Stream02 => 150,
            SlashMode.Fish01 => 60,
            SlashMode.BatteryShow => 50,
            _ => 250
        };

        private void StreamTimer_start(SlashMode mode)
        {
            if (deviceSlash is null) return;

            streamStepMs = StreamStepFor(mode);

            if (slashStreamTimer == default(System.Timers.Timer))
            {
                slashStreamTimer = new System.Timers.Timer(streamStepMs);
                slashStreamTimer.Elapsed += StreamTimer_elapsed;
                slashStreamTimer.AutoReset = true;
            }

            slashStreamTimer.Interval = streamStepMs;

            streamIndex = 0;
            fishTick = 0;
            batteryTick = 0;
            showRng = 0x9E3779B9u;
            sceneDur = 0;
            sceneTicks = 0;
            StreamTimer_tick();
            slashStreamTimer.Start();
        }

        private void StreamTimer_stop()
        {
            slashStreamTimer?.Stop();
        }

        private void StreamTimer_elapsed(object? sender, ElapsedEventArgs e)
        {
            try { StreamTimer_tick(); }
            catch (Exception ex) { Logger.WriteLine(ex.Message); }
        }

        private void StreamTimer_tick()
        {
            if (deviceSlash is null) return;

            int brightness = AppConfig.Get("matrix_brightness", 0);
            var mode = (SlashMode)AppConfig.Get("matrix_running", 0);

            // stop when another mode (or "off") has been selected
            if ((mode != SlashMode.Stream01 && mode != SlashMode.Stream02
                 && mode != SlashMode.Fish01 && mode != SlashMode.BatteryShow)
                || brightness == 0)
            {
                StreamTimer_stop();
                return;
            }

            var payload = new byte[deviceSlash.LedCount];

            if (mode == SlashMode.Fish01)
            {
                BuildCreativeFrame(payload, brightness);
            }
            else if (mode == SlashMode.BatteryShow)
            {
                BuildBatteryFrame(payload, brightness);
            }
            else
            {
                int lit = LitSegmentsForCurrentMode();
                for (int k = 0; k < lit; k++)
                {
                    payload[(streamIndex + k) % payload.Length] = (byte)(brightness * 85.333);
                }
                streamIndex++;
            }

            deviceSlash.ContinueCustom(payload, null);
        }

        // 蓝色大肥鱼01 — a long procedural binary light show (the name is just a name).
        // Only two states per segment: full on / off. Scenes are picked by an LCG, so the
        // sequence keeps evolving for hours instead of looping a short clip.
        // Scene types: chase, fat block, alternating flip, crescendo-to-full, all-on strobe,
        // random bursts, converging pair, full pulses, random sparks. Every scene ends with
        // a short all-black gap for contrast.
        private void BuildCreativeFrame(byte[] payload, int brightness)
        {
            int led = payload.Length;
            fishTick++;

            if (sceneDur == 0 || sceneTicks >= sceneDur) StartShowScene();

            int s = sceneTicks++;
            int idx = Modes.GetCurrent() switch { 2 => 0, 0 => 1, _ => 2 };
            int step = 4 - idx;                     // 4 / 3 / 2 frames per position

            if (s >= sceneDur - 4) return;          // all-black gap between scenes

            byte on = Level(brightness, 1.0);
            long mask = 0;
            long all = (1L << led) - 1;

            switch (sceneType)
            {
                case 0:                             // chase to the right
                    mask = 1L << ((s / step) % led);
                    break;
                case 1:                             // chase to the left
                    mask = 1L << (led - 1 - ((s / step) % led));
                    break;
                case 2:                             // fat 3-segment block
                {
                    int p = (s / step) % led;
                    for (int k = 0; k < 3; k++) mask |= 1L << ((p + k) % led);
                    break;
                }
                case 3:                             // alternating bars flipping
                    mask = ((s / (step + 1)) % 2 == 0) ? 0b1010101L : 0b0101010L;
                    break;
                case 4:                             // crescendo until everything is on
                    mask = (1L << Math.Min(led, s / (step - 1) + 1)) - 1;
                    break;
                case 5:                             // all-on strobe
                    mask = ((s % 7) < 3) ? all : 0;
                    break;
                case 6:                             // random bursts
                    if (s % 5 == 0) sceneMask = RandomMask(led);
                    mask = sceneMask;
                    break;
                case 7:                             // converging pair
                {
                    int p = Math.Min(led / 2, s / step);
                    mask = (1L << p) | (1L << (led - 1 - p));
                    break;
                }
                case 8:                             // full pulses
                    mask = (s < 6 || (s >= 12 && s < 16)) ? all : 0;
                    break;
                default:                            // random sparks
                    if (s % 3 == 0) sceneParam = (int)(NextRand() % (uint)led);
                    mask = 1L << sceneParam;
                    break;
            }

            for (int i = 0; i < led; i++)
            {
                if ((mask & (1L << i)) != 0) payload[i] = on;
            }
        }

        // 电池电量模式 — 7 segments cover 30%..100% (one segment per 10% band).
        // "a" = current charge band (1..7). The bar fills from the physical bottom, so the
        // highest lit segment sits at index (led - a) and descends as the battery drops; it
        // stays solid and never blinks. The cells below it play one lap as:
        //     [1,无] -> [2,1] -> ... -> [a-1,a-2] -> [无,a-1]
        // ([full, 50% trail], positions counted from the bottom). Each step lasts 0.1 s, then the
        // flow rests for 0.5 s (marker only) before the next lap. Timer runs at 50 ms, so a step
        // is 2 frames and the rest is 10 frames. While plugged the full segment climbs bottom ->
        // top; on battery the same lap runs in the opposite direction. Below 40%: marker only.
        private void BuildBatteryFrame(byte[] payload, int brightness)
        {
            int led = payload.Length;
            int t = batteryTick++;

            int percent = (int)Math.Round(HardwareControl.GetBatteryChargePercentage());

            // 30% -> 1 segment, each further 10% adds one, 90..100% -> all segments
            int a = Math.Clamp(1 + (percent - 30) / 10, 1, led);

            byte on = Level(brightness, 1.0);

            // highest lit segment: solid marker for the current level, never blinks
            int marker = led - a;
            payload[marker] = on;

            if (a <= 1) return;

            bool plugged = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;

            int flowFrames = a * 2;              // a steps of 0.1 s at 50 ms/frame
            int cycle = flowFrames + 10;         // + 0.5 s rest
            int frame = t % cycle;
            if (frame >= flowFrames) return;     // rest: marker only

            int s = frame / 2;                   // step index 0..a-1

            // full-brightness cell for this step (positions counted from the bottom)
            if (s <= a - 2)
            {
                int kFull = plugged ? s + 1 : a - 1 - s;
                payload[led - kFull] = on;
            }

            // 50% trail one step behind the full cell
            if (s >= 1)
            {
                int kTrail = plugged ? s : a - s;
                payload[led - kTrail] = Level(brightness, 0.5);
            }
        }

        private void StartShowScene()
        {
            uint r = NextRand();
            sceneType = (int)(r % 10);
            sceneParam = (int)((r >> 4) % 7);
            sceneDur = 26 + (int)((r >> 8) % 42);    // 26..67 frames (~1.6..4 s at 60 ms)
            sceneTicks = 0;
            sceneMask = 0;
        }

        private long RandomMask(int led)
        {
            long mask = 0;
            int count = 1 + (int)(NextRand() % (uint)(led / 2 + 1));
            for (int k = 0; k < count; k++) mask |= 1L << (int)(NextRand() % (uint)led);
            return mask;
        }

        private uint NextRand()
        {
            showRng = showRng * 1664525u + 1013904223u;
            return showRng >> 8;
        }

        private static byte Level(int brightness, double factor)
        {
            int value = (int)(brightness * 85.333 * factor);
            return (byte)Math.Max(0, Math.Min(255, value));
        }

        // Segment count follows the current performance profile:
        // Silent (mode 2) -> 1 LED, Balanced (mode 0) -> 2 LEDs, Turbo / any other mode -> 3 LEDs.
        private static int LitSegmentsForCurrentMode()
        {
            return Modes.GetCurrent() switch
            {
                2 => 1,   // AsusACPI.PerformanceSilent
                0 => 2,   // AsusACPI.PerformanceBalanced
                _ => 3    // Turbo, Full Speed, custom profiles
            };
        }


        public void Dispose()
        {
            disposed = true;
            StopAudio();
            StreamTimer_stop();
            slashStreamTimer?.Dispose();
            matrixTimer?.Stop();
            matrixTimer?.Dispose();
            slashTimer?.Stop();
            slashTimer?.Dispose();
        }

        void StopAudio()
        {
            AudioVisualizer.Shared.Unsubscribe(PresentAudio);
        }

        void SetAudio()
        {
            if (deviceMatrix is not null)
            {
                matrixSpectrogram = AppConfig.Get("matrix_audio_mode", 0) == 1;
                spectroSlices.Clear();
                deviceMatrix.ClearFrames();
                deviceMatrix.SetBuiltInAnimation(false);
            }
            else if (deviceSlash is not null) deviceSlash.SetEmpty();
            else return;

            slashBrightness = AppConfig.Get("matrix_brightness", 0);
            AudioVisualizer.Shared.Subscribe(PresentAudio);
        }

        void PresentAudio(double[] audio)
        {

            if (deviceMatrix is null && deviceSlash is null) return;

            if (Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastPresent) < 30)   return;
            lastPresent = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            int size = 20;
            double[] bars = new double[size];
            double max = 2, maxAverage;

            for (int i = 0; i < size; i++)
            {
                bars[i] = Math.Sqrt(audio[i] * 10000);
                if (bars[i] > max) max = bars[i];
            }

            maxes.Add(max);
            if (maxes.Count > 20) maxes.RemoveAt(0);
            maxAverage = maxes.Average();

            // exception here kills the app
            try
            {
                if (deviceMatrix is not null)
                {
                    if (matrixSpectrogram)
                    {
                        for (int i = 0; i < size; i++) spectroLevels[i] = Math.Max(spectroLevels[i], bars[i]);
                        if (lastPresent - lastSpectro >= 250 && maxes.Count >= 20)
                        {
                            lastSpectro = lastPresent;

                            byte[] slice = new byte[size];
                            for (int i = 0; i < size; i++) slice[i] = (byte)Math.Min(255, Math.Pow(spectroLevels[i] / maxAverage, 2) * 255);
                            Array.Clear(spectroLevels);

                            spectroSlices.Insert(0, slice);
                            int depth = deviceMatrix.MaxColumns + deviceMatrix.FullRows / 2;
                            if (spectroSlices.Count == 1) while (spectroSlices.Count < depth) spectroSlices.Add(slice);
                            if (spectroSlices.Count > depth) spectroSlices.RemoveAt(spectroSlices.Count - 1);

                            deviceMatrix.Clear();
                            for (int i = 0; i < spectroSlices.Count; i++) deviceMatrix.DrawSpectrogramRow(i, spectroSlices[i]);
                            deviceMatrix.Present();
                        }
                    }
                    else
                    {
                        deviceMatrix.Clear();
                        // maxAverage stuck at its floor means noise only, don't draw it
                        if (maxAverage > 2)
                            for (int i = 0; i < size; i++) deviceMatrix.DrawBar(20 - i, bars[i] * 20 / maxAverage);
                        deviceMatrix.Present();
                    }
                }

                if (deviceSlash is not null)
                {
                    if (slashMode == SlashMode.Audio)
                    {
                        var bassLevel = 30 * (bars[0] + bars[1]) / maxAverage;
                        deviceSlash.SetAudioPattern(slashBrightness, bassLevel, 10 * (bars[3] + bars[4] + bars[5] + bars[6]) / maxAverage);
                        //Program.settingsForm.VisualiseAudio(bassLevel);
                    }
                    else
                    {
                        var payload = new byte[7];
                        for (int i = 0; i < 7; i++) payload[6 - i] = (byte)(Math.Min(255, Math.Pow((bars[2 * i] + bars[2 * i + 1]) / 2 / maxAverage, 2) * 0x8F));
                        deviceSlash.ContinueCustom(payload, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }
        }

        public void OpenMatrixPicture()
        {
            string fileName = null;

            Thread t = new Thread(() =>
            {
                OpenFileDialog of = new OpenFileDialog();
                of.Filter = "Image Files (*.bmp;*.jpg;*.jpeg,*.png,*.gif)|*.BMP;*.JPG;*.JPEG;*.PNG;*.GIF";
                if (of.ShowDialog() == DialogResult.OK)
                {
                    fileName = of.FileName;
                }
                return;
            });

            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();

            if (fileName is not null)
            {
                AppConfig.Set("matrix_picture", fileName);
                AppConfig.Set("matrix_running", (int)MatrixMode.Picture);

                SetMatrixPicture(fileName);
                settings.VisualiseMatrixRunning((int)MatrixMode.Picture);

            }

        }

        public void SetMatrixPicture(string fileName)
        {

            if (deviceMatrix is null) return;

            StopMatrixTimer();
            StopAudio();

            try
            {
                using (var fs = new FileStream(fileName, FileMode.Open))
                {
                    using (Image image = Image.FromStream(fs))
                    {
                        ProcessPicture(image);
                        Logger.WriteLine("Matrix " + fileName);
                    }

                    fs.Close();
                    settings.VisualiseMatrixPicture(fileName);
                }
            }
            catch
            {
                Debug.WriteLine("Error loading picture");
                return;
            }

        }

        public void GeneratePictureFrame(Image image, int x, int y)
        {
            int zoom = AppConfig.Get("matrix_zoom", 100);
            int contrast = AppConfig.Get("matrix_contrast", 100);
            int gamma = AppConfig.Get("matrix_gamma", 0);
            InterpolationMode quality = (InterpolationMode)AppConfig.Get("matrix_quality", 0);

            if ((MatrixRotation)AppConfig.Get("matrix_rotation", 0) == MatrixRotation.Planar)
                deviceMatrix.GenerateFrame(image, zoom, x, y, quality, contrast, gamma);
            else
                deviceMatrix.GenerateFrameDiagonal(image, zoom, x, y, quality, contrast, gamma);
        }

        public static int PictureFrameDelay(Image image)
            => Math.Max(AppConfig.Get("matrix_speed", 50), BitConverter.ToInt32(image.GetPropertyItem(0x5100).Value) * 10);

        protected void ProcessPicture(Image image)
        {
            deviceMatrix.SetBuiltInAnimation(false);
            deviceMatrix.ClearFrames();

            int matrixX = AppConfig.Get("matrix_x", 0);
            int matrixY = AppConfig.Get("matrix_y", 0);

            FrameDimension dimension = new FrameDimension(image.FrameDimensionsList[0]);
            int frameCount = image.GetFrameCount(dimension);

            if (frameCount > 1)
            {
                for (int i = 0; i < frameCount; i++)
                {
                    image.SelectActiveFrame(dimension, i);
                    GeneratePictureFrame(image, matrixX, matrixY);
                    deviceMatrix.AddFrame();
                }

                int frameDelay = PictureFrameDelay(image);
                Logger.WriteLine("GIF Delay:" + frameDelay + " Frames:" + frameCount);
                StartMatrixTimer(frameDelay);
            }
            else
            {
                GeneratePictureFrame(image, matrixX, matrixY);
                deviceMatrix.Present();
            }

        }

    }
}
