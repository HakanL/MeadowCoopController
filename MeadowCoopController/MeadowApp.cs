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
        private const int pwmFrequency = 1000;

        private RgbPwmLed onboardLed;
        private IPwmPort coopLightsPwm;
        private IPwmPort buttonLightPwm;
        private IPwmPort treeLightsPwm;
        private PwmLed coopLights;
        private PwmLed buttonLight;
        private PwmLed treeLights;
        private IDigitalInputPort pushButton;
        private bool? lastPushButtonStatus;
        private bool? lightsOverride;
        private bool? lastCoopLightsTimerState;
        private bool? lastTreeLightsTimerState;
        private bool coopLightsOn;
        private bool treeLightsOn;
        private Settings settings;
        private CancellationTokenSource buttonCts;
        private bool abortLongHold;

        public MeadowApp()
        {
            Console.WriteLine($"Machine Name: {Environment.MachineName}");

            Initialize();

            Start();
        }

        private async void Initialize()
        {
            Console.WriteLine("Initialize hardware...");

            this.onboardLed = new RgbPwmLed(device: Device,
                redPwmPin: Device.Pins.OnboardLedRed,
                greenPwmPin: Device.Pins.OnboardLedGreen,
                bluePwmPin: Device.Pins.OnboardLedBlue,
                Meadow.Peripherals.Leds.IRgbLed.CommonType.CommonAnode);

            this.coopLightsPwm = Device.CreatePwmPort(Device.Pins.D05, frequency: pwmFrequency);
            this.coopLights = new PwmLed(this.coopLightsPwm, TypicalForwardVoltage.ResistorLimited);
            this.coopLightsPwm.Frequency = pwmFrequency;

            this.buttonLightPwm = Device.CreatePwmPort(Device.Pins.D03, frequency: pwmFrequency);
            this.buttonLight = new PwmLed(this.buttonLightPwm, TypicalForwardVoltage.ResistorLimited);
            this.buttonLightPwm.Frequency = pwmFrequency;

            this.treeLightsPwm = Device.CreatePwmPort(Device.Pins.D10, frequency: pwmFrequency);
            this.treeLights = new PwmLed(this.treeLightsPwm, TypicalForwardVoltage.ResistorLimited);
            this.treeLightsPwm.Frequency = pwmFrequency;

            this.pushButton = Device.CreateDigitalInputPort(Device.Pins.D00, InterruptMode.EdgeBoth, ResistorMode.InternalPullUp, debounceDuration: 50, glitchDuration: 25);
            this.pushButton.Changed += PushButton_Changed;

            this.coopLightsPwm.Start();
            this.buttonLightPwm.Start();
            this.treeLightsPwm.Start();

            Console.WriteLine("Reading config file...");
            try
            {
                string settingsFilename = Path.Combine(MeadowOS.FileSystem.DocumentsDirectory, "settings.json");

                this.settings = new Settings();

                if (File.Exists(settingsFilename))
                {
                    var x = SimpleJsonSerializer.JsonSerializer.DeserializeString(File.ReadAllText(settingsFilename)) as Hashtable;

                    this.settings.CoopBrightness = ReadSettings(x, "CoopBrightness", 0.5F);
                    this.settings.TreeBrightness = ReadSettings(x, "TreeBrightness", 0.5F);
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

            Console.WriteLine($"Coop lights brightness = {this.settings.CoopBrightness:P0}");

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

        private void PushButton_Changed(object sender, DigitalPortResult e)
        {
            //Console.WriteLine($"Push button 2 new status {e.New.State}, old status {e.Old?.State}");

            if (this.lastPushButtonStatus.HasValue)
            {
                if (this.lastPushButtonStatus.Value == e.New.State)
                    return;
            }

            if(e.New.State == false)
            {
                // Pressed
                PushButton_PressStarted(this.pushButton, EventArgs.Empty);
            }
            else
            {
                // Depressed
                PushButton_PressEnded(this.pushButton, EventArgs.Empty);
            }

            this.lastPushButtonStatus = e.New.State;
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

                        float currentBrightness = this.settings.CoopBrightness;
                        float adder = 0.005F;
                        int lastBrightness = (int)(currentBrightness * 100.0);

                        while (!cancelToken.IsCancellationRequested)
                        {
                            currentBrightness += adder;
                            if (currentBrightness > 1.0F || currentBrightness < 0.01F)
                                adder = -adder;

                            if (currentBrightness > 1F)
                                currentBrightness = 1F;
                            if (currentBrightness < 0.01F)
                                currentBrightness = 0.01F;

                            int reportBrightness = (int)(currentBrightness * 100.0);
                            if (lastBrightness != reportBrightness)
                            {
                                Console.WriteLine($"Brightness = {currentBrightness:P0}");
                                lastBrightness = reportBrightness;
                            }

                            this.coopLights.Brightness = currentBrightness;
                            await Task.Delay(20);
                        }

                        if (!this.abortLongHold)
                        {
                            Console.WriteLine($"Set brightness to {currentBrightness:P0}");
                            this.settings.CoopBrightness = currentBrightness;

                            // Flash the lights
                            this.coopLights.Brightness = 0;
                            await Task.Delay(500);
                            this.coopLights.Brightness = this.settings.CoopBrightness;
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

            if (this.buttonCts != null)
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
                    ["CoopBrightness"] = this.settings.CoopBrightness,
                    ["TreeBrightness"] = this.settings.TreeBrightness
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
            this.treeLightsOn = this.lightsOverride ?? false;
            SetLights();

            Console.WriteLine($"Coop lights are now {(this.coopLightsOn ? "On" : "Off")}");
        }

        void Start()
        {
            while (true)
            {
                int TimeZoneOffSet = -5; // CST
                var now = DateTime.Now.AddHours(TimeZoneOffSet);

                bool coopLightsOnTimer = now.Hour >= 19 && now.Hour < 22;
                bool treeLightsOnTimer = now.Hour >= 19 || now.Hour < 7;
                //lightsOnTimer = now.Hour >= 15 && now.Hour < 16;        // Test

                Console.WriteLine($"It is now {now} and the coop lights timer is {(coopLightsOnTimer ? "On" : "Off")} and tree lights timer is {(treeLightsOnTimer ? "On" : "Off")}");

                if (!this.lastCoopLightsTimerState.HasValue || this.lastCoopLightsTimerState != coopLightsOnTimer)
                {
                    // It has changed
                    Console.WriteLine($"Lights on Coop timer has changed state. Old State = {lastCoopLightsTimerState}");

                    this.lightsOverride = null;

                    if (this.buttonCts == null)
                        ButtonLightActive();
                    this.coopLightsOn = coopLightsOnTimer;

                    SetLights();
                }

                if (!this.lastTreeLightsTimerState.HasValue || this.lastTreeLightsTimerState != treeLightsOnTimer)
                {
                    // It has changed
                    Console.WriteLine($"Lights on Tree timer has changed state. Old State = {lastTreeLightsTimerState}");

                    this.treeLightsOn = treeLightsOnTimer;

                    SetLights();
                }

                this.lastCoopLightsTimerState = coopLightsOnTimer;
                this.lastTreeLightsTimerState = treeLightsOnTimer;

                Thread.Sleep(10000);
            }
        }

        private void SetLights()
        {
            if (this.coopLightsOn)
                this.coopLights.Brightness = this.settings.CoopBrightness;
            else
                this.coopLights.Brightness = 0;

            if (this.treeLightsOn)
                this.treeLights.Brightness = this.settings.TreeBrightness;
            else
                this.treeLights.Brightness = 0;
        }

        private void WiFiAdapter_ConnectionCompleted(object sender, EventArgs e)
        {
            this.onboardLed.Stop();
            this.onboardLed.SetColor(Color.Green, 0.5F);

            Console.WriteLine("Connection request completed.");
        }
    }
}
