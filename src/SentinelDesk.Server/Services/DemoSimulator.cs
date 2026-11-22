using Microsoft.EntityFrameworkCore;
using SentinelDesk.Server.Data;
using SentinelDesk.Shared;

namespace SentinelDesk.Server.Services;

/// <summary>
/// Generates a realistic simulated fleet (island-wide branches) so the console can be evaluated
/// without deploying agents. Data flows through the real ingestion + heartbeat pipeline.
/// Enable with Demo:Enabled=true. Simulated devices are flagged IsSimulated and can be purged from the UI.
/// </summary>
public sealed class DemoSimulator(IServiceScopeFactory scopes, IConfiguration config, ILogger<DemoSimulator> log) : BackgroundService
{
    private static readonly (string Name, string Code, double Lat, double Lng)[] Branches =
    [
        ("Colombo HQ", "CMB", 6.9271, 79.8612), ("Kandy", "KDY", 7.2906, 80.6337), ("Galle", "GAL", 6.0535, 80.2210),
        ("Jaffna", "JAF", 9.6615, 80.0255), ("Kurunegala", "KUR", 7.4863, 80.3647), ("Anuradhapura", "ANU", 8.3114, 80.4037),
        ("Batticaloa", "BAT", 7.7310, 81.6747), ("Trincomalee", "TRI", 8.5874, 81.2152), ("Ratnapura", "RAT", 6.6828, 80.3992),
        ("Matara", "MAT", 5.9549, 80.5550), ("Badulla", "BAD", 6.9934, 81.0550), ("Negombo", "NEG", 7.2008, 79.8737),
    ];

    private static readonly string[] Depts = ["FIN", "HR", "ENG", "OPS", "IT", "CS", "ADM"];
    private static readonly string[] Users =
    [
        "nimal.perera", "kasun.silva", "dilani.fernando", "tharindu.jayasinghe", "sanduni.wickramasinghe", "ruwan.bandara",
        "priyanka.rajapaksa", "chamara.dissanayake", "ishara.gunawardena", "malith.herath", "nadeesha.kumari", "suresh.kumar",
        "anushka.rathnayake", "lahiru.senanayake", "thilini.weerasinghe", "mohamed.rizwan", "kavindu.abeysekara", "shanika.peiris",
        "vimukthi.de.silva", "ganesh.sivakumar", "harsha.mendis", "dinusha.karunaratne", "chathura.ekanayake", "yasodha.liyanage"
    ];

    private static readonly (string App, string Title, int Weight)[] Apps =
    [
        ("chrome.exe", "Google Chrome", 22), ("msedge.exe", "Microsoft Edge", 14), ("OUTLOOK.EXE", "Inbox - Outlook", 14),
        ("EXCEL.EXE", "Monthly Billing Summary.xlsx - Excel", 16), ("WINWORD.EXE", "Board Paper Draft.docx - Word", 9),
        ("ms-teams.exe", "Microsoft Teams", 10), ("saplogon.exe", "SAP Easy Access", 8), ("AcroRd32.exe", "Tender Document.pdf - Adobe Acrobat", 5),
        ("explorer.exe", "File Explorer", 4), ("POWERPNT.EXE", "Quarterly Review.pptx - PowerPoint", 3), ("WhatsApp.exe", "WhatsApp", 3),
        ("Code.exe", "Visual Studio Code", 2), ("AutoCAD.exe", "Pipeline Layout.dwg - AutoCAD", 2), ("vlc.exe", "VLC media player", 1),
    ];

    private static readonly (string Domain, int Weight)[] Sites =
    [
        ("outlook.office.com", 18), ("teams.microsoft.com", 14), ("www.google.com", 14), ("company.sharepoint.com", 10),
        ("www.youtube.com", 8), ("web.whatsapp.com", 7), ("www.facebook.com", 6), ("mail.gov.lk", 5), ("www.dailymirror.lk", 4),
        ("chatgpt.com", 4), ("www.adaderana.lk", 3), ("www.daraz.lk", 2), ("www.linkedin.com", 3), ("github.com", 2),
        ("stackoverflow.com", 2), ("www.netflix.com", 1), ("www.tiktok.com", 2), ("www.ikman.lk", 1), ("www.cbsl.gov.lk", 2),
        ("www.roblox.com", 1), ("drive.google.com", 2), ("www.dropbox.com", 1), ("ctldl.windowsupdate.com", 6), ("www.msftconnecttest.com", 3),
    ];

    private static readonly SoftwareItem[] BaseSoftware =
    [
        new("Microsoft 365 Apps for enterprise - en-us", "16.0.18129.20158", "Microsoft Corporation", "20250310"),
        new("Google Chrome", "131.0.6778.86", "Google LLC", "20251102"),
        new("Microsoft Edge", "131.0.2903.70", "Microsoft Corporation", "20251105"),
        new("Adobe Acrobat Reader (64-bit)", "24.004.20220", "Adobe", "20250812"),
        new("7-Zip 24.08 (x64)", "24.08", "Igor Pavlov", "20250101"),
        new("Microsoft Teams", "24295.605.3225.8804", "Microsoft Corporation", "20250915"),
        new("SAP GUI for Windows 8.00", "8.00 Patch 12", "SAP SE", "20240601"),
        new("ESET Endpoint Antivirus", "11.1.2039.2", "ESET, spol. s r.o.", "20250210"),
        new("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "14.40.33810", "Microsoft Corporation", "20240722"),
        new("VLC media player", "3.0.21", "VideoLAN", "20250405"),
        new("SentinelDesk Agent", "1.0.0", "SentinelDesk", "20261101"),
    ];

    private static readonly SoftwareItem[] ExtraSoftware =
    [
        new("AnyDesk", "9.0.2", "AnyDesk Software GmbH", "20261012"), new("Zoom Workplace (64-bit)", "6.2.11", "Zoom Video Communications, Inc.", "20251001"),
        new("Notepad++ (64-bit x64)", "8.7.1", "Notepad++ Team", "20250918"), new("AutoCAD 2024", "24.3.61.0", "Autodesk", "20240311"),
        new("Visual Studio Code", "1.95.3", "Microsoft Corporation", "20251111"), new("WhatsApp", "2.2450.6", "WhatsApp Inc.", "20251020"),
        new("uTorrent", "3.6.0", "BitTorrent Inc.", "20261115"), new("Python 3.12.7 (64-bit)", "3.12.7", "Python Software Foundation", "20251001"),
    ];

    private static readonly (string Maker, string Model, string Cpu, int Ram)[] Models =
    [
        ("Dell Inc.", "OptiPlex 7090", "Intel(R) Core(TM) i5-11500 @ 2.70GHz", 16384), ("HP", "ProDesk 400 G7 MT", "Intel(R) Core(TM) i5-10500 @ 3.10GHz", 8192),
        ("LENOVO", "ThinkPad T14 Gen 3", "12th Gen Intel(R) Core(TM) i7-1255U", 16384), ("HP", "EliteBook 840 G8", "11th Gen Intel(R) Core(TM) i5-1135G7", 16384),
        ("Dell Inc.", "Latitude 5440", "13th Gen Intel(R) Core(TM) i5-1335U", 16384), ("LENOVO", "ThinkCentre M70q", "Intel(R) Core(TM) i3-10100T", 8192),
    ];

    private sealed class Sim
    {
        public required Device Device;
        public required string User;
        public bool Online;
        public double Cpu, Ram, Disk;
        public double NetFactor;
        public string App = "chrome.exe";
        public string Title = "";
        public long TodayIn, TodayOut;
        public DateOnly TodayDay;
        public bool Laptop;
    }

    private readonly List<Sim> _sims = [];
    private readonly Random _rng = new(42);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Demo:Enabled", false))
        {
            // Live-only mode: clear out any simulated fleet left from an earlier demo run.
            using var scope = scopes.CreateScope();
            var n = await PurgeAsync(scope.ServiceProvider.GetRequiredService<AppDb>());
            if (n > 0) log.LogInformation("Demo mode is off - removed {Count} simulated devices and their data", n);
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        try { await EnsureFleetAsync(ct); }
        catch (Exception ex) { log.LogError(ex, "Demo fleet setup failed"); return; }

        var tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                tick++;
                await HeartbeatTickAsync(ct);
                if (tick % 12 == 0) await MinuteTickAsync(ct);
                if (tick % 360 == 0) ToggleSomeOnline();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Demo tick failed"); }
        }
    }

    /// <summary>Removes all simulated devices, their telemetry/alerts, and the demo branches/regions left empty.</summary>
    public static async Task<int> PurgeAsync(AppDb db)
    {
        var ids = await db.Devices.Where(d => d.IsSimulated).Select(d => d.Id).ToListAsync();
        await Endpoints.DeviceEndpoints.DeleteDevicesAsync(db, ids);

        // the demo moved "Head Office" (renamed Colombo HQ) under a demo region — put it back under the root
        var root = await db.OrgUnits.FirstOrDefaultAsync(o => o.ParentId == null);
        var hq = await db.OrgUnits.FirstOrDefaultAsync(o => o.Name == "Colombo HQ");
        if (hq is not null && root is not null && hq.ParentId != root.Id
            && await db.OrgUnits.AnyAsync(o => o.Id == hq.ParentId && DemoRegions.Contains(o.Name)))
        {
            hq.ParentId = root.Id;
            await db.SaveChangesAsync();
        }

        // demo-created org units that are now empty (never touches units holding real devices or children)
        var demoNames = Branches.Select(b => b.Name).Concat(DemoRegions).ToHashSet();
        for (var pass = 0; pass < 3; pass++)
        {
            var units = await db.OrgUnits.ToListAsync();
            var used = (await db.Devices.Where(d => d.OrgUnitId != null).Select(d => d.OrgUnitId!.Value).Distinct().ToListAsync()).ToHashSet();
            var empty = units.Where(u => u.ParentId != null && demoNames.Contains(u.Name) && u.Name != "Colombo HQ"
                                         && !used.Contains(u.Id) && units.All(c => c.ParentId != u.Id)).ToList();
            if (empty.Count == 0) break;
            var emptyIds = empty.Select(u => u.Id).ToList();
            await db.PolicyAssignments.Where(a => a.TargetType == "orgUnit" && emptyIds.Contains(a.TargetId)).ExecuteDeleteAsync();
            db.OrgUnits.RemoveRange(empty);
            await db.SaveChangesAsync();
        }
        return ids.Count;
    }

    private static readonly string[] DemoRegions =
        ["Western Province", "Central Province", "Southern Province", "Northern Province", "Eastern Province", "North Western & North Central", "Uva & Sabaragamuwa"];

    private async Task EnsureFleetAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var ingest = scope.ServiceProvider.GetRequiredService<IngestService>();

        var existing = await db.Devices.Where(d => d.IsSimulated).ToListAsync(ct);
        if (existing.Count == 0)
        {
            log.LogInformation("Demo mode: creating simulated island-wide fleet (this takes a few seconds)…");
            var root = await db.OrgUnits.FirstAsync(o => o.ParentId == null, ct);
            var regions = new Dictionary<string, OrgUnit>();
            foreach (var r in new[] { "Western Province", "Central Province", "Southern Province", "Northern Province", "Eastern Province", "North Western & North Central", "Uva & Sabaragamuwa" })
            {
                var ou = new OrgUnit { ParentId = root.Id, Name = r, Type = "region" };
                regions[r] = ou;
                db.OrgUnits.Add(ou);
            }
            string RegionOf(string code) => code switch
            {
                "CMB" or "NEG" => "Western Province", "KDY" => "Central Province", "GAL" or "MAT" => "Southern Province",
                "JAF" => "Northern Province", "BAT" or "TRI" => "Eastern Province", "KUR" or "ANU" => "North Western & North Central",
                _ => "Uva & Sabaragamuwa"
            };
            var headOffice = await db.OrgUnits.FirstOrDefaultAsync(o => o.Name == "Head Office", ct);
            var created = new List<Device>();
            var userIdx = 0;
            foreach (var b in Branches)
            {
                var branch = b.Code == "CMB" && headOffice is not null ? headOffice : new OrgUnit { Name = b.Name, Type = "branch", Lat = b.Lat, Lng = b.Lng };
                branch.ParentId = regions[RegionOf(b.Code)].Id;
                if (b.Code == "CMB") { branch.Name = "Colombo HQ"; branch.Lat = b.Lat; branch.Lng = b.Lng; }
                if (branch != headOffice) db.OrgUnits.Add(branch);
                var count = b.Code == "CMB" ? 9 : _rng.Next(2, 5);
                for (var i = 0; i < count; i++)
                {
                    var m = Models[_rng.Next(Models.Length)];
                    var laptop = m.Model.Contains("ThinkPad") || m.Model.Contains("EliteBook") || m.Model.Contains("Latitude");
                    var dept = Depts[_rng.Next(Depts.Length)];
                    var d = new Device
                    {
                        OrgUnitId = branch.Id, IsSimulated = true, MachineId = Guid.NewGuid().ToString("N"),
                        Hostname = $"{b.Code}-{dept}-{(laptop ? "LT" : "PC")}{_rng.Next(1, 120):000}", Domain = "CORP",
                        Serial = $"{(char)('A' + _rng.Next(26))}{_rng.Next(1000000, 9999999)}", Manufacturer = m.Maker, Model = m.Model,
                        Os = _rng.NextDouble() < 0.8 ? "Windows 11 Pro" : "Windows 10 Pro", OsBuild = _rng.NextDouble() < 0.8 ? "26100.2314" : "19045.5131",
                        AgentVersion = _rng.NextDouble() < 0.85 ? "1.0.0" : "0.9.4", Status = DeviceStatus.Active,
                        EnrolledAt = DateTime.UtcNow.AddDays(-_rng.Next(8, 60)), LastIp = $"10.{_rng.Next(1, 40)}.{_rng.Next(0, 255)}.{_rng.Next(2, 250)}",
                        LoggedInUser = $"CORP\\{Users[userIdx++ % Users.Length]}", Tags = laptop ? "laptop" : "desktop",
                        CertThumbprint = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + "SIMU"
                    };
                    d.LastSeenAt = DateTime.UtcNow.AddMinutes(-_rng.Next(1, 5));
                    db.Devices.Add(d);
                    created.Add(d);
                }
            }
            await db.SaveChangesAsync(ct);

            // assign the restricted policy to a finance branch for illustration
            var restricted = await db.Policies.FirstOrDefaultAsync(p => !p.IsDefault, ct);
            var gal = await db.OrgUnits.FirstOrDefaultAsync(o => o.Name == "Galle", ct);
            if (restricted is not null && gal is not null)
                db.PolicyAssignments.Add(new PolicyAssignment { PolicyId = restricted.Id, TargetType = "orgUnit", TargetId = gal.Id });
            await db.SaveChangesAsync(ct);

            foreach (var d in created) await BackfillAsync(ingest, d, ct);
            existing = created;
            log.LogInformation("Demo mode: {Count} simulated devices with 14 days of history ready", created.Count);
        }

        foreach (var d in existing)
        {
            var laptop = d.Tags?.Contains("laptop") == true;
            _sims.Add(new Sim
            {
                Device = d, User = d.LoggedInUser ?? "CORP\\user", Online = _rng.NextDouble() > (laptop ? 0.35 : 0.12),
                Cpu = _rng.Next(5, 35), Ram = _rng.Next(35, 75), Disk = _rng.Next(30, 94), NetFactor = 0.3 + _rng.NextDouble() * (_rng.NextDouble() < 0.1 ? 6 : 1.6),
                Laptop = laptop, TodayDay = IngestService.Today()
            });
        }
        // seed today's counters from the rollup
        var today = IngestService.Today();
        var daily = await db.Daily.AsNoTracking().Where(x => x.Day == today).ToDictionaryAsync(x => x.DeviceId, ct);
        foreach (var s in _sims)
            if (daily.TryGetValue(s.Device.Id, out var du)) { s.TodayIn = du.BytesIn; s.TodayOut = du.BytesOut; }
    }

    private async Task BackfillAsync(IngestService ingest, Device d, CancellationToken ct)
    {
        var laptop = d.Tags?.Contains("laptop") == true;
        var heavy = _rng.NextDouble() < 0.12 ? 4.0 : 1.0;
        var start = DateTime.UtcNow.Date.AddDays(-13);
        var user = d.LoggedInUser;
        for (var day = 0; day <= 13; day++)
        {
            var payload = new TelemetryPayload();
            for (var h = 0; h < 24; h++)
            {
                var t = start.AddDays(day).AddHours(h);
                if (t > DateTime.UtcNow) break;
                var local = TimeZoneInfo.ConvertTimeFromUtc(t, IngestService.OrgTz);
                var weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                var work = !weekend && local.Hour is >= 8 and < 17;
                var on = work || (laptop && _rng.NextDouble() < 0.15) || (!laptop && _rng.NextDouble() < 0.05);
                if (!on) continue;
                var mbIn = (work ? _rng.Next(10, 130) : _rng.Next(1, 25)) * heavy;
                var mbOut = mbIn * (0.08 + _rng.NextDouble() * 0.15);
                payload.Adapters.Add(new AdapterSample(t, laptop ? "Wi-Fi" : "Ethernet", laptop ? "Wi-Fi" : "LAN", (long)(mbIn * 1048576), (long)(mbOut * 1048576)));
                payload.Perf.Add(new PerfSample(t, _rng.Next(4, work ? 65 : 20), _rng.Next(40, 85), _rng.Next(40, 85)));
                if (work)
                {
                    var remaining = 3600;
                    foreach (var app in PickApps(4))
                    {
                        var active = Math.Min(remaining, _rng.Next(300, 1500));
                        remaining -= active;
                        payload.AppUsage.Add(new AppUsageSample(t, user, app.App, app.Title, active, 0));
                    }
                    payload.AppUsage.Add(new AppUsageSample(t, user, "(idle)", null, 0, Math.Max(0, remaining)));
                    foreach (var s in PickSites(6))
                        payload.Web.Add(new WebSample(t, user, s, "chrome.exe", _rng.Next(1, 30)));
                    payload.ProcessNet.Add(new ProcessNetSample(t, "chrome.exe", (long)(mbIn * 0.55 * 1048576), (long)(mbOut * 0.4 * 1048576)));
                    payload.ProcessNet.Add(new ProcessNetSample(t, "ms-teams.exe", (long)(mbIn * 0.25 * 1048576), (long)(mbOut * 0.45 * 1048576)));
                    payload.ProcessNet.Add(new ProcessNetSample(t, "OUTLOOK.EXE", (long)(mbIn * 0.12 * 1048576), (long)(mbOut * 0.1 * 1048576)));
                    payload.ProcessNet.Add(new ProcessNetSample(t, "svchost.exe", (long)(mbIn * 0.08 * 1048576), (long)(mbOut * 0.05 * 1048576)));
                }
                if (h == 3 && !weekend) payload.Events.Add(new AgentEvent(local.Date.AddHours(8).AddMinutes(_rng.Next(0, 45)).ToUniversalTime(), "session.logon", "info", $"{user} signed in"));
                if (_rng.NextDouble() < 0.01) payload.Events.Add(new AgentEvent(t, "usb.inserted", "info", "USB mass storage: SanDisk Ultra 32GB", new() { ["serial"] = "4C530001" + _rng.Next(1000, 9999) }));
            }
            await ingest.IngestAsync(d.Id, payload, ct);
        }

        var software = BaseSoftware.Concat(ExtraSoftware.Where(x => _rng.NextDouble() < (x.Name is "uTorrent" or "AnyDesk" ? 0.06 : 0.3))).ToList();
        var model = Models.FirstOrDefault(m => m.Model == d.Model);
        await ingest.IngestAsync(d.Id, new TelemetryPayload
        {
            Software = software,
            Hardware = new HardwareInfo
            {
                Manufacturer = d.Manufacturer, Model = d.Model, Serial = d.Serial, BiosVersion = "1.24.0", Cpu = model.Cpu ?? "Intel Core i5",
                CpuCores = 6, CpuThreads = 12, RamMb = model.Ram == 0 ? 8192 : model.Ram, Os = d.Os, OsBuild = d.OsBuild, Domain = "CORP", TpmPresent = true,
                Gpu = "Intel(R) UHD Graphics 770",
                Disks = [new DiskInfo("C:", "NVMe SAMSUNG MZVL2512", 476, _rng.Next(40, 300), "SSD")],
                Nics = [new NicInfo(laptop ? "Wi-Fi" : "Ethernet", $"3C:52:82:{_rng.Next(16, 255):X2}:{_rng.Next(16, 255):X2}:{_rng.Next(16, 255):X2}", laptop ? "Wireless80211" : "Ethernet", d.LastIp, laptop ? 866 : 1000)]
            },
            Security = new SecurityPosture
            {
                Antivirus = "ESET Endpoint Antivirus", AntivirusEnabled = _rng.NextDouble() > 0.05, AntivirusUpToDate = _rng.NextDouble() > 0.1,
                FirewallEnabled = _rng.NextDouble() > 0.08, BitLocker = laptop ? (_rng.NextDouble() > 0.2 ? "On" : "Off") : "Off",
                LastUpdateInstalled = DateTime.UtcNow.AddDays(-_rng.Next(2, 45)), RebootPending = _rng.NextDouble() < 0.2, UsbStorageBlocked = false
            },
            Events = [new AgentEvent(d.EnrolledAt, "agent.enrolled", "info", "Agent enrolled")]
        }, ct);
    }

    private async Task HeartbeatTickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var hb = scope.ServiceProvider.GetRequiredService<HeartbeatService>();
        var today = IngestService.Today();
        foreach (var s in _sims.Where(s => s.Online))
        {
            if (s.TodayDay != today) { s.TodayDay = today; s.TodayIn = s.TodayOut = 0; }
            s.Cpu = Math.Clamp(s.Cpu + _rng.Next(-8, 9), 2, 99);
            s.Ram = Math.Clamp(s.Ram + _rng.Next(-2, 3), 25, 95);
            if (_rng.NextDouble() < 0.08) { var a = PickApps(1)[0]; s.App = a.App; s.Title = a.Title; }
            var burst = _rng.NextDouble() < 0.1 ? 8 : 1;
            var inBps = (long)(_rng.Next(5_000, 400_000) * s.NetFactor * burst);
            var outBps = (long)(inBps * (0.1 + _rng.NextDouble() * 0.3));
            s.TodayIn += inBps * 5; s.TodayOut += outBps * 5;
            var idle = _rng.NextDouble() < 0.12;
            await hb.ApplyAsync(db, s.Device, new HeartbeatRequest
            {
                AgentVersion = s.Device.AgentVersion, LoggedInUser = s.User, LocalIp = s.Device.LastIp, UptimeSec = 3600 * 5 + _rng.Next(0, 3600),
                Cpu = s.Cpu, Ram = s.Ram, Disk = s.Disk, NetInBps = inBps, NetOutBps = outBps, TodayBytesIn = s.TodayIn, TodayBytesOut = s.TodayOut,
                CurrentApp = idle ? null : s.App, CurrentTitle = idle ? null : s.Title, UserIdle = idle, TrayRunning = true,
                ConnectionType = s.Laptop ? "Wi-Fi" : "LAN", PolicyVersion = 1
            }, null, true);
        }
    }

    private async Task MinuteTickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var ingest = scope.ServiceProvider.GetRequiredService<IngestService>();
        var t = DateTime.UtcNow;
        foreach (var s in _sims.Where(s => s.Online))
        {
            var p = new TelemetryPayload();
            var mbIn = _rng.Next(1, 5) * s.NetFactor;
            p.Adapters.Add(new AdapterSample(t, s.Laptop ? "Wi-Fi" : "Ethernet", s.Laptop ? "Wi-Fi" : "LAN", (long)(mbIn * 1048576), (long)(mbIn * 0.15 * 1048576)));
            p.Perf.Add(new PerfSample(t, s.Cpu, s.Ram, s.Disk));
            var active = _rng.Next(20, 60);
            p.AppUsage.Add(new AppUsageSample(t, s.User, s.App, s.Title, active, 0));
            p.AppUsage.Add(new AppUsageSample(t, s.User, "(idle)", null, 0, 60 - active));
            p.ProcessNet.Add(new ProcessNetSample(t, s.App, (long)(mbIn * 0.7 * 1048576), (long)(mbIn * 0.1 * 1048576)));
            foreach (var site in PickSites(2)) p.Web.Add(new WebSample(t, s.User, site, "chrome.exe", _rng.Next(1, 6)));
            var r = _rng.NextDouble();
            if (r < 0.004) p.Events.Add(new AgentEvent(t, "usb.inserted", "info", "USB mass storage: Kingston DataTraveler 64GB", new() { ["serial"] = "001A92" + _rng.Next(1000, 9999) }));
            else if (r < 0.006) p.Events.Add(new AgentEvent(t, "tamper.serviceStop", "critical", $"Attempt to stop SentinelDesk service by {s.User} was blocked"));
            else if (r < 0.03) p.Events.Add(new AgentEvent(t, _rng.NextDouble() < 0.5 ? "session.lock" : "session.unlock", "info", $"Workstation {(_rng.NextDouble() < 0.5 ? "locked" : "unlocked")} by {s.User}"));
            await ingest.IngestAsync(s.Device.Id, p, ct);
        }
    }

    private void ToggleSomeOnline()
    {
        foreach (var s in _sims)
            if (_rng.NextDouble() < 0.05) s.Online = !s.Online;
    }

    private List<(string App, string Title)> PickApps(int n)
    {
        var total = Apps.Sum(a => a.Weight);
        var result = new List<(string, string)>();
        for (var i = 0; i < n; i++)
        {
            var r = _rng.Next(total);
            foreach (var a in Apps) { if ((r -= a.Weight) < 0) { if (!result.Contains((a.App, a.Title))) result.Add((a.App, a.Title)); break; } }
        }
        return result.Count == 0 ? [("chrome.exe", "Google Chrome")] : result;
    }

    private List<string> PickSites(int n)
    {
        var total = Sites.Sum(a => a.Weight);
        var result = new HashSet<string>();
        for (var i = 0; i < n; i++)
        {
            var r = _rng.Next(total);
            foreach (var s in Sites) { if ((r -= s.Weight) < 0) { result.Add(s.Domain); break; } }
        }
        return result.ToList();
    }
}
