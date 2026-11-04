using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Interfaces;
using System.Globalization;

namespace RIoT2.Net.Devices.Abstracts
{
    /// <summary>
    /// A eufy account's stations and cameras: the templates, reports and commands the <see cref="EufySecurity"/> (C#
    /// port of eufy-sdk) and <see cref="EufySecuritySdk"/> (eufy-sdk itself) devices share. Only the service differs.
    /// </summary>
    public abstract class EufySecurityBase : AsyncDeviceBase<EufySecurityOptions>, IDeviceWithConfiguration, IAsyncCommandDevice
    {
        /// <summary>The address of the account's connection status report.</summary>
        public const string StatusAddress = "eufy|status";
        /// <summary>The address of the command that answers a captcha or verification code.</summary>
        public const string VerifyAddress = "eufy|verify";
        private const string GuardModeSuffix = "|guardMode";
        private const string AlarmSuffix = "|alarm";
        private const string SnapshotSuffix = "|snapshot";

        private readonly IEufySecurityService _eufySecurityService;
        private readonly IMemoryStorageService _memoryStorageService;

        protected EufySecurityBase(ILogger logger, IEufySecurityService eufySecurityService, IMemoryStorageService memoryStorageService) : base(logger)
        {
            _memoryStorageService = memoryStorageService;
            _eufySecurityService = eufySecurityService;
        }

        /// <summary>The name of the configuration template.</summary>
        protected abstract string TemplateName { get; }

        /// <summary>The folder of the session and push registration files; tests replace it.</summary>
        internal string DataDirectory { get; set; } = "Data";

        public override void ConfigureDevice()
        {
            _eufySecurityService.Configure(new EufyAccountOptions
            {
                Email = Options.Email.Trim(),
                Password = Options.Password,
                CountryCode = string.IsNullOrWhiteSpace(Options.CountryCode) ? "US" : Options.CountryCode.Trim().ToUpperInvariant(),
                FfmpegPath = string.IsNullOrWhiteSpace(Options.FfmpegPath) ? "ffmpeg" : Options.FfmpegPath.Trim(),
                DataDirectory = DataDirectory
            });
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = TemplateName,
                ClassFullName = GetType().FullName!,
                DeviceParameters = new Dictionary<string, string>
                {
                    ["email"] = "",
                    ["password"] = "",
                    ["countryCode"] = "US",
                    ["ffmpegPath"] = "ffmpeg"
                }
            };

            var reports = new List<ReportTemplate>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = StatusAddress,
                    Name = "Eufy account status",
                    Type = Core.ValueType.Entity,
                    Model = new ValueModel(new EufyStatusReport { State = nameof(EufyConnectionState.Connected) })
                }
            };
            var commands = new List<CommandTemplate>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = VerifyAddress,
                    Name = "Answer eufy captcha or verification code",
                    Type = Core.ValueType.Text,
                    Model = "123456"
                }
            };

            // The stations and devices are known once the account is connected; build from the cached list.
            var devices = _eufySecurityService.Devices;
            foreach (var station in devices.Where(d => d.IsStation))
            {
                reports.Add(new ReportTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = station.Serial + GuardModeSuffix,
                    Name = $"{station.Name} guard mode",
                    Type = Core.ValueType.Number,
                    Model = 1
                });
                reports.Add(new ReportTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = station.Serial + AlarmSuffix,
                    Name = $"{station.Name} alarm",
                    Type = Core.ValueType.Entity,
                    Model = new ValueModel(new EufyAlarmReport { Phase = "triggered", AlarmType = 0, DeviceSerial = station.Serial })
                });
                commands.Add(new CommandTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = station.Serial + GuardModeSuffix,
                    Name = $"Set {station.Name} guard mode",
                    Type = Core.ValueType.Number,
                    Model = 1
                });
            }

            foreach (var camera in Cameras(devices))
            {
                reports.Add(new ReportTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = camera.Serial,
                    Name = camera.Name,
                    Type = Core.ValueType.Entity,
                    Model = new ValueModel(new SecurityReport
                    {
                        ImageUrl = "http://image-url",
                        Source = "eufy",
                        EventValue = "event value",
                        SecurityEvent = SecurityEventType.motionDetected
                    })
                });
                commands.Add(new CommandTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = camera.Serial + SnapshotSuffix,
                    Name = $"Take {camera.Name} snapshot",
                    Type = Core.ValueType.Boolean,
                    Model = true
                });
            }

            deviceConfiguration.ReportTemplates = reports;
            deviceConfiguration.CommandTemplates = commands;
            return deviceConfiguration;
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            if (command.Address == VerifyAddress)
            {
                await _eufySecurityService.SubmitVerificationAsync(value, cancellationToken).ConfigureAwait(false);
            }
            else if (command.Address.EndsWith(GuardModeSuffix, StringComparison.Ordinal))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode))
                    throw new ArgumentException($"The guard mode '{value}' is not a number.", nameof(value));
                await _eufySecurityService.SetGuardModeAsync(command.Address[..^GuardModeSuffix.Length], mode, cancellationToken).ConfigureAwait(false);
            }
            else if (command.Address.EndsWith(SnapshotSuffix, StringComparison.Ordinal))
            {
                var serial = command.Address[..^SnapshotSuffix.Length];
                var jpeg = await _eufySecurityService.SnapshotAsync(serial, cancellationToken).ConfigureAwait(false);
                SendSecurityReport(serial, SecurityEventType.snapshot, "snapshot", "Snapshot", jpeg);
            }
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            Logger.LogInformation("Starting {Device} device...", TemplateName);
            _eufySecurityService.SecurityEvent += OnSecurityEvent;
            _eufySecurityService.GuardModeChanged += OnGuardModeChanged;
            _eufySecurityService.StatusChanged += OnStatusChanged;
            await _eufySecurityService.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override async Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _eufySecurityService.SecurityEvent -= OnSecurityEvent;
            _eufySecurityService.GuardModeChanged -= OnGuardModeChanged;
            _eufySecurityService.StatusChanged -= OnStatusChanged;
            await _eufySecurityService.StopAsync().ConfigureAwait(false);
        }

        // A scheduled refresh reads the guard modes again (the device list carries them).
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report == null || report.Address.EndsWith(GuardModeSuffix, StringComparison.Ordinal)
                ? _eufySecurityService.RefreshAsync(cancellationToken)
                : Task.CompletedTask;

        // Every device that is not the station of another one has a camera (or sensor) of its own.
        private static IEnumerable<EufyDeviceRecord> Cameras(IReadOnlyList<EufyDeviceRecord> devices)
        {
            var hubs = devices.Where(d => !d.IsStation).Select(d => d.StationSerial).ToHashSet();
            return devices.Where(d => !hubs.Contains(d.Serial));
        }

        private void OnStatusChanged(EufyServiceStatus status)
        {
            var template = Template(StatusAddress);
            if (template == null)
                return;

            string? captchaUrl = null;
            if (status.CaptchaImage is { } image && TryDecodeDataUrl(image, out var png))
                captchaUrl = SavePicture(template.Id, png, DocumentType.Photo);

            SendReport(this, new Report
            {
                Id = template.Id,
                Value = new ValueModel(new EufyStatusReport
                {
                    State = status.State.ToString(),
                    Message = status.Message,
                    CaptchaImageUrl = captchaUrl,
                    TwoFactorMethod = status.TwoFactorMethod
                }),
                TimeStamp = DateTime.UtcNow.ToEpoch()
            });
        }

        private void OnGuardModeChanged(string stationSerial, int mode)
        {
            var template = Template(stationSerial + GuardModeSuffix);
            if (template == null)
                return;

            SendReport(this, new Report
            {
                Id = template.Id,
                Value = new ValueModel(mode),
                TimeStamp = DateTime.UtcNow.ToEpoch()
            });
        }

        private void OnSecurityEvent(EufySecurityEvent securityEvent, byte[]? picture)
        {
            if (securityEvent.Kind == EufyEventKind.Alarm)
            {
                var stationSerial = securityEvent.StationSerial ?? StationOf(securityEvent.DeviceSerial);
                var template = stationSerial == null ? null : Template(stationSerial + AlarmSuffix);
                if (template == null)
                    return;

                SendReport(this, new Report
                {
                    Id = template.Id,
                    Value = new ValueModel(new EufyAlarmReport
                    {
                        Phase = (securityEvent.AlarmPhase ?? EufyAlarmPhase.Triggered).ToString().ToLowerInvariant(),
                        AlarmType = securityEvent.AlarmType,
                        DeviceSerial = securityEvent.DeviceSerial
                    }),
                    TimeStamp = DateTime.UtcNow.ToEpoch()
                });
                return;
            }

            var serial = securityEvent.DeviceSerial ?? securityEvent.StationSerial;
            if (serial == null || !TryMap(securityEvent, out var type, out var value, out var message))
                return;

            SendSecurityReport(serial, type, value, message, picture);
        }

        private void SendSecurityReport(string serial, SecurityEventType type, object? value, string message, byte[]? picture)
        {
            var template = Template(serial);
            if (template == null)
                return;

            SendReport(this, new Report
            {
                Id = template.Id,
                Value = new ValueModel(new SecurityReport
                {
                    ImageUrl = picture == null ? null : SavePicture(template.Id, picture, DocumentType.Photo),
                    Source = "eufy",
                    Message = message,
                    EventValue = value,
                    SecurityEvent = type
                }),
                TimeStamp = DateTime.UtcNow.ToEpoch()
            });
        }

        internal static bool TryMap(EufySecurityEvent securityEvent, out SecurityEventType type, out object? value, out string message)
        {
            value = true;
            (type, message) = securityEvent.Kind switch
            {
                EufyEventKind.Motion => (SecurityEventType.motionDetected, "Motion detected"),
                EufyEventKind.PersonDetected when !string.IsNullOrEmpty(securityEvent.PersonName) => (SecurityEventType.personName, $"{securityEvent.PersonName} detected"),
                EufyEventKind.PersonDetected => (SecurityEventType.personDetected, "Person detected"),
                EufyEventKind.StrangerDetected => (SecurityEventType.strangerPersonDetected, "Stranger detected"),
                EufyEventKind.PetDetected => (SecurityEventType.animalDetected, "Pet detected"),
                EufyEventKind.DogDetected => (SecurityEventType.animalDetected, "Dog detected"),
                EufyEventKind.SoundDetected => (SecurityEventType.soundDetected, "Sound detected"),
                EufyEventKind.CryingDetected => (SecurityEventType.cryingDetected, "Crying detected"),
                EufyEventKind.VehicleDetected => (SecurityEventType.vehicleDetected, "Vehicle detected"),
                EufyEventKind.DoorbellPress => (SecurityEventType.doorbellPressed, "Doorbell pressed"),
                EufyEventKind.PackageDelivered => (SecurityEventType.packageDelivered, "Package delivered"),
                EufyEventKind.PackageTaken => (SecurityEventType.packageTaken, "Package taken"),
                EufyEventKind.PackageStranded => (SecurityEventType.packageStranded, "Package left unattended"),
                _ => ((SecurityEventType)(-1), "")
            };

            if (securityEvent.Kind == EufyEventKind.PersonDetected && !string.IsNullOrEmpty(securityEvent.PersonName))
                value = securityEvent.PersonName;
            else if (securityEvent.Kind == EufyEventKind.DogDetected)
                value = "dog";

            return (int)type >= 0;
        }

        private string? StationOf(string? deviceSerial) =>
            deviceSerial == null ? null : _eufySecurityService.Devices.FirstOrDefault(d => d.Serial == deviceSerial)?.StationSerial;

        private ReportTemplate? Template(string address) => ReportTemplates?.FirstOrDefault(x => x.Address == address);

        private string SavePicture(string address, byte[] data, DocumentType type) =>
            _memoryStorageService.Save(new Document
            {
                Data = data,
                Epochmt = DateTime.UtcNow.ToEpoch(),
                Filename = Guid.NewGuid().ToString(),
                Isfolder = FileOrFolder.File,
                Filetype = type,
                Filesize = data.Length.ToString(CultureInfo.InvariantCulture),
                Properties = new Dictionary<string, string>()
            }, address);

        private static bool TryDecodeDataUrl(string dataUrl, out byte[] data)
        {
            data = [];
            var comma = dataUrl.IndexOf(',');
            try
            {
                data = Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl);
                return data.Length > 0;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
