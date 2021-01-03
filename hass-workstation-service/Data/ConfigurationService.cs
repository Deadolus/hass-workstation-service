using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading.Tasks;
using hass_workstation_service.Communication;
using hass_workstation_service.Communication.InterProcesCommunication.Models;
using hass_workstation_service.Communication.NamedPipe;
using hass_workstation_service.Domain.Sensors;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using Serilog;

namespace hass_workstation_service.Data
{
    public class ConfigurationService : IConfigurationService
    {
        public ICollection<AbstractSensor> ConfiguredSensors { get; private set; }
        public Action<IMqttClientOptions> MqqtConfigChangedHandler { get; set; }

        public bool _brokerSettingsFileLocked { get; set; }
        public bool _sensorsSettingsFileLocked { get; set; }

        private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hass Workstation Service");

        public ConfigurationService()
        {
            if (!File.Exists(Path.Combine(path, "mqttbroker.json")))
            {
                File.Create(Path.Combine(path, "mqttbroker.json"));
            }

            if (!File.Exists(Path.Combine(path, "configured-sensors.json")))
            {
                File.Create(Path.Combine(path, "configured-sensors.json"));
            }

            ConfiguredSensors = new List<AbstractSensor>();
        }

        public async void ReadSensorSettings(MqttPublisher publisher)
        {
            while (this._sensorsSettingsFileLocked)
            {
                await Task.Delay(500);
            }
            this._sensorsSettingsFileLocked = true;
            List<ConfiguredSensor> sensors = new List<ConfiguredSensor>();
            using (var stream = new FileStream(Path.Combine(path, "configured-sensors.json"), FileMode.Open))
            {
                Log.Logger.Information($"reading configured sensors from: {stream.Name}");
                if (stream.Length > 0)
                {
                    sensors = await JsonSerializer.DeserializeAsync<List<ConfiguredSensor>>(stream);
                }
                stream.Close();
                this._sensorsSettingsFileLocked = false;
            }

            foreach (ConfiguredSensor configuredSensor in sensors)
            {
                AbstractSensor sensor = null;
                switch (configuredSensor.Type)
                {
                    case "UserNotificationStateSensor":
                        sensor = new UserNotificationStateSensor(publisher, configuredSensor.UpdateInterval, configuredSensor.Name, configuredSensor.Id);
                        break;
                    case "DummySensor":
                        sensor = new DummySensor(publisher, configuredSensor.UpdateInterval, configuredSensor.Name, configuredSensor.Id);
                        break;
                    case "CurrentClockSpeedSensor":
                        sensor = new CurrentClockSpeedSensor(publisher, configuredSensor.UpdateInterval, configuredSensor.Name, configuredSensor.Id);
                        break;
                    case "WMIQuerySensor":
                        sensor = new WMIQuerySensor(publisher, configuredSensor.Query, configuredSensor.UpdateInterval, configuredSensor.Name, configuredSensor.Id);
                        break;
                    case "CPULoadSensor":
                        sensor = new CPULoadSensor(publisher, configuredSensor.UpdateInterval, configuredSensor.Name, configuredSensor.Id);
                        break;
                    case "MemoryUsageSensor":
                        sensor = new MemoryUsageSensor(publisher, configuredSensor.UpdateInterval, configuredSensor.Name, configuredSensor.Id);
                        break;
                    case "ActiveWindowSensor":
                        sensor = new ActiveWindowSensor(publisher, configuredSensor.UpdateInterval, configuredSensor.Name, configuredSensor.Id);
                        break;
                    case "WebcamActiveSensor":
                        sensor = new WebcamActiveSensor(publisher, configuredSensor.UpdateInterval, configuredSensor.Name, configuredSensor.Id);
                        break;
                    default:
                        Log.Logger.Error("unsupported sensor type in config");
                        break;
                }
                if (sensor != null)
                {
                    this.ConfiguredSensors.Add(sensor);
                }
            }
        }

        public async Task<IMqttClientOptions> GetMqttClientOptionsAsync()
        {
            ConfiguredMqttBroker configuredBroker = await ReadMqttSettingsAsync();
            if (configuredBroker != null && configuredBroker.Host != null)
            {

                var mqttClientOptions = new MqttClientOptionsBuilder()
                    .WithTcpServer(configuredBroker.Host)
                    // .WithTls()
                    .WithCredentials(configuredBroker.Username, configuredBroker.Password.ToString())
                    .Build();
                return mqttClientOptions;
            }
            else
            {
                Program.StartUI();
                return null;
            }
        }

        /// <summary>
        /// Gets the saved broker settings from configfile. Null if not found.
        /// </summary>
        /// <returns></returns>
        public async Task<ConfiguredMqttBroker> ReadMqttSettingsAsync()
        {
            while (this._brokerSettingsFileLocked)
            {
                await Task.Delay(500);
            }
            this._brokerSettingsFileLocked = true;
            ConfiguredMqttBroker configuredBroker = null;
            using (FileStream stream = new FileStream(Path.Combine(path, "mqttbroker.json"), FileMode.Open))
            {
                Log.Logger.Information($"reading configured mqttbroker from: {stream.Name}");
                if (stream.Length > 0)
                {
                    configuredBroker = await JsonSerializer.DeserializeAsync<ConfiguredMqttBroker>(stream);
                }
                stream.Close();
            }

            this._brokerSettingsFileLocked = false;
            return configuredBroker;
        }

        public async void WriteSettingsAsync()
        {
            while (this._sensorsSettingsFileLocked)
            {
                await Task.Delay(500);
            }
            this._sensorsSettingsFileLocked = true;
            List<ConfiguredSensor> configuredSensorsToSave = new List<ConfiguredSensor>();
            using (FileStream stream = new FileStream(Path.Combine(path, "configured-sensors.json"), FileMode.Open))
            {
                stream.SetLength(0);
                Log.Logger.Information($"writing configured sensors to: {stream.Name}");
                foreach (AbstractSensor sensor in this.ConfiguredSensors)
                {
                    if (sensor is WMIQuerySensor)
                    {
                        var wmiSensor = (WMIQuerySensor)sensor;
                        configuredSensorsToSave.Add(new ConfiguredSensor() { Id = wmiSensor.Id, Name = wmiSensor.Name, Type = wmiSensor.GetType().Name, UpdateInterval = wmiSensor.UpdateInterval, Query = wmiSensor.Query });
                    }
                    else
                    {
                        configuredSensorsToSave.Add(new ConfiguredSensor() { Id = sensor.Id, Name = sensor.Name, Type = sensor.GetType().Name, UpdateInterval = sensor.UpdateInterval });
                    }

                }

                await JsonSerializer.SerializeAsync(stream, configuredSensorsToSave);
                stream.Close();
            }
            this._sensorsSettingsFileLocked = false;
        }

        public void AddConfiguredSensor(AbstractSensor sensor)
        {
            this.ConfiguredSensors.Add(sensor);
            sensor.PublishAutoDiscoveryConfigAsync();
            WriteSettingsAsync();
        }

        public async void DeleteConfiguredSensor(Guid id)
        {
            var sensorToRemove = this.ConfiguredSensors.FirstOrDefault(s => s.Id == id);
            if (sensorToRemove != null)
            {
                await sensorToRemove.UnPublishAutoDiscoveryConfigAsync();
                this.ConfiguredSensors.Remove(sensorToRemove);
                WriteSettingsAsync();
            }
            else
            {
                Log.Logger.Warning($"sensor with id {id} not found");
            }

        }

        public void AddConfiguredSensors(List<AbstractSensor> sensors)
        {
            sensors.ForEach((sensor) => this.ConfiguredSensors.Add(sensor));
            WriteSettingsAsync();
        }

        /// <summary>
        /// Writes provided settings to the config file and invokes a reconfigure to the current mqqtClient
        /// </summary>
        /// <param name="settings"></param>
        public async void WriteMqttBrokerSettingsAsync(MqttSettings settings)
        {
            while (this._brokerSettingsFileLocked)
            {
                await Task.Delay(500);
            }
            this._brokerSettingsFileLocked = true;
            using (FileStream stream = new FileStream(Path.Combine(path, "mqttbroker.json"), FileMode.Open))
            {
                stream.SetLength(0);
                Log.Logger.Information($"writing configured mqttbroker to: {stream.Name}");

                ConfiguredMqttBroker configuredBroker = new ConfiguredMqttBroker()
                {
                    Host = settings.Host,
                    Username = settings.Username,
                    Password = settings.Password
                };

                await JsonSerializer.SerializeAsync(stream, configuredBroker);
                stream.Close();
            }
            this._brokerSettingsFileLocked = false;
            this.MqqtConfigChangedHandler.Invoke(await this.GetMqttClientOptionsAsync());
        }

        public async Task<MqttSettings> GetMqttBrokerSettings()
        {
            ConfiguredMqttBroker broker = await ReadMqttSettingsAsync();
            return new MqttSettings
            {
                Host = broker?.Host,
                Username = broker?.Username,
                Password = broker?.Password
            };
        }

        /// <summary>
        /// Enable or disable autostarting the background service. It does this by adding the application shortcut (appref-ms) to the registry run key for the current user
        /// </summary>
        /// <param name="enable"></param>
        public void EnableAutoStart(bool enable)
        {
            if (enable)
            {
                Log.Logger.Information("configuring autostart");
                // The path to the key where Windows looks for startup applications
                RegistryKey rkApp = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

                //Path to launch shortcut
                string startPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs) + @"\Sleevezipper\Hass Workstation Service.appref-ms";

                rkApp.SetValue("hass-workstation-service", startPath);
                rkApp.Close();
            }
            else
            {
                Log.Logger.Information("removing autostart");
                RegistryKey rkApp = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                rkApp.DeleteValue("hass-workstation-service");
                rkApp.Close();
            }
        }

        public bool IsAutoStartEnabled()
        {
            RegistryKey rkApp = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            return rkApp.GetValue("hass-workstation-service") != null;
        }
    }
}