#define DISABLE_WIFI

using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meadow;
using Meadow.Devices;
using Meadow.Foundation;
using Meadow.Foundation.Leds;
using Meadow.Foundation.Sensors.Buttons;
using Meadow.Gateway.WiFi;
using Meadow.Hardware;

namespace MeadowCoopController
{
    // Change F7FeatherV2 to F7FeatherV1 for V1.x boards
    public class MeadowApp : App<F7FeatherV1, MeadowApp>
    {
        private readonly TimeSpan longPressThreshold = TimeSpan.FromMilliseconds(2000);

        RgbPwmLed onboardLed;
        IPwmPort coopLightsPwm;
        IPwmPort buttonLightPwm;
        PwmLed coopLights;
        PwmLed buttonLight;
        PushButton pushButton;
        private bool? lightsOverride;
        private bool? lastLightsTimerState;
        private bool coopLightsOn;
        private Settings settings;
        private CancellationTokenSource buttonCts;
        private bool abortLongHold;

        public MeadowApp()
        {
            Console.WriteLine($"Machine Name: {Environment.MachineName}");

            Initialize();

            Start();
        }

        async void Initialize()
        {
            Console.WriteLine("Initialize hardware...");

            this.onboardLed = new RgbPwmLed(device: Device,
                redPwmPin: Device.Pins.OnboardLedRed,
                greenPwmPin: Device.Pins.OnboardLedGreen,
                bluePwmPin: Device.Pins.OnboardLedBlue,
                Meadow.Peripherals.Leds.IRgbLed.CommonType.CommonAnode);

            this.coopLightsPwm = Device.CreatePwmPort(Device.Pins.D04);
            this.coopLights = new PwmLed(this.coopLightsPwm, TypicalForwardVoltage.Green);

            this.buttonLightPwm = Device.CreatePwmPort(Device.Pins.D03);
            this.buttonLight = new PwmLed(this.buttonLightPwm, TypicalForwardVoltage.Blue);

            this.pushButton = new PushButton(
                Device,
                Device.Pins.D02,
                ResistorMode.InternalPullUp);

            this.pushButton.PressStarted += PushButton_PressStarted;
            this.pushButton.PressEnded += PushButton_PressEnded;

            this.coopLightsPwm.Start();
            this.buttonLightPwm.Start();

            Console.WriteLine("Reading config file...");
            try
            {
                string settingsFilename = Path.Combine(MeadowOS.FileSystem.DocumentsDirectory, "settings.json");

                this.settings = new Settings();

                if (File.Exists(settingsFilename))
                {
                    var x = SimpleJsonSerializer.JsonSerializer.DeserializeString(File.ReadAllText(settingsFilename)) as Hashtable;

                    this.settings.Brightness = ReadSettings(x, "Brightness", 0.5F);
                }
                else
                    SaveSettings();
            }
            catch (Exception ex)
            {
                // Ignore
                this.settings = new Settings();

                Console.WriteLine($"Unable to deserialize settings file: {ex.Message}");
                SaveSettings();
            }

            Console.WriteLine($"Coop lights brightness = {this.settings.Brightness:P0}");

            this.onboardLed.StartPulse(Color.Orange, 0.5F, 0.1F);

            Device.WiFiAdapter.WiFiConnected += WiFiAdapter_ConnectionCompleted;

            Console.WriteLine("Connecting to WiFi network...");

            // Set external antenna
            Device.SetAntenna(Meadow.Gateways.AntennaType.External);

#if !DISABLE_WIFI
            var connectionResult = await Device.WiFiAdapter.Connect(Secrets.WIFI_NAME, Secrets.WIFI_PASSWORD, TimeSpan.FromSeconds(60));

            if (connectionResult.ConnectionStatus != ConnectionStatus.Success)
            {
                this.onboardLed.Stop();
                this.onboardLed.SetColor(Color.Red);

                throw new Exception($"Cannot connect to network: {connectionResult.ConnectionStatus}");
            }
            else
            {
                Console.WriteLine($"IP Address: {Device.WiFiAdapter.IpAddress}");
                Console.WriteLine($"Subnet mask: {Device.WiFiAdapter.SubnetMask}");
                Console.WriteLine($"Gateway: {Device.WiFiAdapter.Gateway}");
            }
#endif

            ButtonLightActive();
        }

        private async Task ButtonPressHandler(CancellationToken cancelToken)
        {
            ButtonLightOff();

            await Task.Delay(this.longPressThreshold, cancelToken).ContinueWith(async t =>
            {
                this.abortLongHold = false;

                if (t.IsCanceled)
                {
                    // Short click
                    PushButtonShortClicked();
                }
                else
                {
                    // Long hold
                    Console.WriteLine("Long hold");

                    if (this.coopLightsOn)
                    {
                        Console.WriteLine("Set brightness");
                        this.buttonLight.Stop();
                        this.buttonLight.Brightness = 0.1F;

                        // Set brightness if the lights are on

                        float currentBrightness = this.settings.Brightness;
                        float adder = 0.02F;

                        while (!cancelToken.IsCancellationRequested)
                        {
                            currentBrightness += adder;
                            if (currentBrightness > 1.0F || currentBrightness < 0.01F)
                                adder = -adder;

                            if (currentBrightness > 1F)
                                currentBrightness = 1F;
                            if (currentBrightness < 0.01F)
                                currentBrightness = 0.01F;

                            Console.WriteLine($"Brightness = {currentBrightness:P0}");

                            this.coopLights.Brightness = currentBrightness;
                            await Task.Delay(100);
                        }

                        if (!this.abortLongHold)
                        {
                            Console.WriteLine($"Set brightness to {currentBrightness:P0}");
                            this.settings.Brightness = currentBrightness;

                            // Flash the lights
                            this.coopLights.Brightness = 0;
                            await Task.Delay(500);
                            this.coopLights.Brightness = this.settings.Brightness;
                            await Task.Delay(500);
                            this.coopLights.Brightness = 0;
                            await Task.Delay(500);
                            SaveSettings();
                        }

                        SetLights();
                    }
                    else
                    {
                        Console.WriteLine("Do nothing");
                    }
                }

                ButtonLightActive();
            });
        }

        private void PushButton_PressStarted(object sender, EventArgs e)
        {
            Console.WriteLine("Pressed");

            if(this.buttonCts != null)
            {
                // We lost an unpressed event
                Console.WriteLine("Missed unpressed event");
                this.abortLongHold = true;
                this.buttonCts.Cancel();
            }

            var cts = new CancellationTokenSource();
            this.buttonCts = cts;
            Task.Run(() => ButtonPressHandler(cts.Token));
        }

        private void PushButton_PressEnded(object sender, EventArgs e)
        {
            Console.WriteLine("Unpressed");

            this.buttonCts?.Cancel();
            this.buttonCts = null;
        }

        private float ReadSettings(Hashtable settings, string key, float defaultValue = 0)
        {
            object value = settings[key];
            if (value == null)
                return defaultValue;

            if (value is double @doubleValue)
                return (float)@doubleValue;

            return defaultValue;
        }

        private void SaveSettings()
        {
            try
            {
                string settingsFilename = Path.Combine(MeadowOS.FileSystem.DocumentsDirectory, "settings.json");

                var jsonData = new Hashtable
                {
                    ["Brightness"] = this.settings.Brightness
                };

                File.WriteAllText(settingsFilename, SimpleJsonSerializer.JsonSerializer.SerializeObject(jsonData));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unable to write settings file: {ex.Message}");
            }
        }

        private void ButtonLightActive()
        {
            if (this.lightsOverride.HasValue)
            {
                this.buttonLight.Stop();
                this.buttonLight.Brightness = this.coopLightsOn ? 1.0F : 0.5F;
            }
            else
            {
                this.buttonLight.StartPulse(0.4F, 0.01F);
            }
        }

        private void ButtonLightOff()
        {
            this.buttonLight.Stop();
            this.buttonLight.Brightness = 0;
        }

        private void PushButtonShortClicked()
        {
            Console.WriteLine("Button short clicked");

            if (this.lightsOverride.HasValue)
            {
                this.lightsOverride = !this.lightsOverride;
            }
            else
            {
                this.lightsOverride = !this.coopLightsOn;
            }

            this.coopLightsOn = this.lightsOverride ?? false;
            SetLights();
        }

        void Start()
        {
            while (true)
            {
                int TimeZoneOffSet = -5; // CST
                var now = DateTime.Now.AddHours(TimeZoneOffSet);

                bool lightsOnTimer = now.Hour >= 19 && now.Hour < 23;
                //lightsOnTimer = now.Hour >= 15 && now.Hour < 16;        // Test

                Console.WriteLine($"It is now {now} and the lights timer is {(lightsOnTimer ? "On" : "Off")}");

                if (!this.lastLightsTimerState.HasValue || this.lastLightsTimerState != lightsOnTimer)
                {
                    // It has changed
                    Console.WriteLine($"Lights on timer has changed state. Old State = {lastLightsTimerState}");

                    this.lightsOverride = null;

                    if (this.buttonCts == null)
                        ButtonLightActive();
                    this.coopLightsOn = lightsOnTimer;

                    SetLights();
                }

                this.lastLightsTimerState = lightsOnTimer;

                Thread.Sleep(10000);
            }
        }

        private void SetLights()
        {
            if (this.coopLightsOn)
                this.coopLights.Brightness = this.settings.Brightness;
            else
                this.coopLights.Brightness = 0;
        }

        private void WiFiAdapter_ConnectionCompleted(object sender, EventArgs e)
        {
            this.onboardLed.Stop();
            this.onboardLed.SetColor(Color.Green, 0.5F);

            Console.WriteLine("Connection request completed.");
        }
    }
}
