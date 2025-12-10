using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Mail;

// Alias para evitar conflictos de Timer
using Timer = System.Timers.Timer;
using ElapsedEventArgs = System.Timers.ElapsedEventArgs;

namespace ZuriFocus.Monitor
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "ZuriFocus Monitor - Sesión con activo/idle, apps y sitios";
            Console.WriteLine("ZuriFocus Monitor iniciado...");
            Console.WriteLine();

            DateTime today = DateTime.Today;
            string computerId = Environment.MachineName;
            string windowsUser = Environment.UserName;


            // 1) Cargar configuración
            MonitorSettings settings = LoadSettings();

            // 2) Intentar enviar reporte del último día pendiente
            MaybeSendWeeklyReport(settings, computerId);

            // 3) Cargar DayLog de hoy y continuar como ya lo tenías
            DayLog todayLog = LoadDayLogOrCreate(today, computerId, windowsUser);

            // 4) Crear sesión
            Session currentSession = new Session
            {
                Start = DateTime.Now
            };

            Console.WriteLine($"Equipo : {todayLog.ComputerId}");
            Console.WriteLine($"Usuario: {todayLog.WindowsUser}");
            Console.WriteLine($"Inicio : {currentSession.Start:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine();
            Console.WriteLine("Monitoreando sesión actual...");
            Console.WriteLine(" - Idle : si pasan 60 s sin mover mouse/teclado -> inactivo.");
            Console.WriteLine(" - Apps : se mide qué proceso está en primer plano.");
            Console.WriteLine(" - Web  : se mide sitio activo usando el título de la pestaña.");
            Console.WriteLine("Cuando quieras terminar la sesión, presiona ENTER.");
            Console.WriteLine();

            Stopwatch stopwatch = Stopwatch.StartNew();

            var idleTracker = new IdleTracker(settings.IdleThresholdSeconds);
            var appTracker = new AppTracker();
            var websiteTracker = new WebsiteTracker();

            idleTracker.Start();
            appTracker.Start();
            websiteTracker.Start();

            Console.ReadLine(); // Esperamos fin de la sesión

            // Parar trackers
            stopwatch.Stop();
            idleTracker.Stop();
            appTracker.Stop();
            websiteTracker.Stop();

            currentSession.End = DateTime.Now;
            currentSession.ActiveMinutes = (int)Math.Round(idleTracker.ActiveSeconds / 60.0);
            currentSession.IdleMinutes = (int)Math.Round(idleTracker.IdleSeconds / 60.0);

            // 3) Agregar sesión al DayLog
            todayLog.Sessions.Add(currentSession);

            // 4) Aplicaciones
            List<AppUsage> appUsages = appTracker.GetAppUsageMinutes();
            MergeApplications(todayLog, appUsages);

            // 5) Sitios web
            List<WebsiteUsage> websiteUsages = websiteTracker.GetWebsiteUsageMinutes();
            MergeWebsites(todayLog, websiteUsages);

            // 6) Guardar
            SaveDayLog(todayLog);

            // --- Resumen en consola ---
            Console.WriteLine();
            Console.WriteLine("Sesión registrada:");
            Console.WriteLine($"  Inicio      : {currentSession.Start:HH:mm:ss}");
            Console.WriteLine($"  Fin         : {currentSession.End:HH:mm:ss}");
            Console.WriteLine($"  Total       : {currentSession.TotalMinutes} min");
            Console.WriteLine($"  Activo      : {currentSession.ActiveMinutes} min");
            Console.WriteLine($"  Idle (aprox): {currentSession.IdleMinutes} min");
            Console.WriteLine();

            Console.WriteLine("Uso de aplicaciones en esta ejecución:");
            foreach (var app in appUsages)
            {
                Console.WriteLine($"  {app.ProcessName,-20} -> {app.TotalMinutes} min");
            }
            Console.WriteLine();

            Console.WriteLine("Sitios web en esta ejecución:");
            foreach (var site in websiteUsages)
            {
                Console.WriteLine($"  {site.Domain,-30} -> {site.TotalMinutes} min");
            }

            Console.WriteLine();
            Console.WriteLine("Log actualizado en la carpeta 'logs'. Presiona ENTER para salir.");
            Console.ReadLine();
        }

        // ==================== Manejo de DayLog ====================

        private static string GetLogFilePath(DateTime date, string computerId)
        {
            string logsFolder = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsFolder);

            string fileName = $"{date:yyyy-MM-dd}-{computerId}.json";
            return Path.Combine(logsFolder, fileName);
        }

        private static DayLog LoadDayLogOrCreate(DateTime date, string computerId, string windowsUser)
        {
            string filePath = GetLogFilePath(date, computerId);

            if (File.Exists(filePath))
            {
                try
                {
                    string json = File.ReadAllText(filePath);
                    var existing = JsonSerializer.Deserialize<DayLog>(json);
                    if (existing != null)
                    {
                        existing.WindowsUser = windowsUser;
                        return existing;
                    }
                }
                catch
                {
                    // si falla, creamos uno nuevo
                }
            }

            return new DayLog
            {
                Date = date,
                ComputerId = computerId,
                WindowsUser = windowsUser
            };
        }

        private static void SaveDayLog(DayLog log)
        {
            string filePath = GetLogFilePath(log.Date, log.ComputerId);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string json = JsonSerializer.Serialize(log, options);
            File.WriteAllText(filePath, json);
        }

        private static MonitorSettings LoadSettings()
        {
            string filePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

            // Si no existe, creamos uno con valores por defecto
            if (!File.Exists(filePath))
            {
                var defaultSettings = new MonitorSettings();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string defaultJson = JsonSerializer.Serialize(defaultSettings, options);
                File.WriteAllText(filePath, defaultJson);

                Console.WriteLine("No se encontró settings.json. Se creó uno con valores por defecto.");
                Console.WriteLine($"Umbral de idle: {defaultSettings.IdleThresholdSeconds} segundos");
                return defaultSettings;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<MonitorSettings>(json);
                if (settings == null)
                    throw new Exception("settings.json deserializado como null.");

                Console.WriteLine($"Configuración cargada. Umbral de idle: {settings.IdleThresholdSeconds} segundos");
                return settings;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al leer settings.json. Usando valores por defecto.");
                Console.WriteLine(ex.Message);
                return new MonitorSettings();
            }
        }

        private static EmailState LoadEmailState()
        {
            string filePath = Path.Combine(AppContext.BaseDirectory, "email_state.json");

            if (!File.Exists(filePath))
            {
                return new EmailState(); // sin fecha aún
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var state = JsonSerializer.Deserialize<EmailState>(json);
                return state ?? new EmailState();
            }
            catch
            {
                return new EmailState();
            }
        }

        private static void SaveEmailState(EmailState state)
        {
            string filePath = Path.Combine(AppContext.BaseDirectory, "email_state.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(state, options);
            File.WriteAllText(filePath, json);
        }

        private static DayLog? LoadDayLogFromFile(DateTime date, string computerId)
        {
            string filePath = GetLogFilePath(date, computerId);
            if (!File.Exists(filePath)) return null;

            try
            {
                string json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<DayLog>(json);
            }
            catch
            {
                return null;
            }
        }

        private static string BuildHtmlReportBody(WeeklyReportData weekly)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("<html><body>");
            sb.AppendLine($"<h2>Reporte semanal ZuriFocus - {weekly.ComputerId}</h2>");
            sb.AppendLine($"<p><strong>Semana:</strong> {weekly.WeekStart:yyyy-MM-dd} al {weekly.WeekEnd:yyyy-MM-dd}</p>");

            if (!string.IsNullOrWhiteSpace(weekly.WindowsUser))
            {
                sb.AppendLine($"<p><strong>Usuario principal:</strong> {weekly.WindowsUser}</p>");
            }

            sb.AppendLine("<h3>Sesiones agregadas por día (lunes a domingo)</h3>");
            sb.AppendLine("<table border='1' cellpadding='4' cellspacing='0'>");
            sb.AppendLine("<tr><th>#</th><th>Día</th><th>Encendido (h)</th><th>Activo (h)</th><th>Idle (h)</th></tr>");

            double totalOnHours = 0;
            double totalActiveHours = 0;
            double totalIdleHours = 0;

            for (int i = 0; i < weekly.Days.Count; i++)
            {
                var d = weekly.Days[i];

                double onH = d.TotalOnMinutes / 60.0;
                double actH = d.ActiveMinutes / 60.0;
                double idleH = d.IdleMinutes / 60.0;

                totalOnHours += onH;
                totalActiveHours += actH;
                totalIdleHours += idleH;

                string dayName = GetSpanishDayName(d.Date.DayOfWeek);

                sb.AppendLine("<tr>");
                sb.AppendLine($"<td>{i + 1}</td>");
                sb.AppendLine($"<td>{dayName} {d.Date:dd/MM}</td>");
                sb.AppendLine($"<td>{onH:F2}</td>");
                sb.AppendLine($"<td>{actH:F2}</td>");
                sb.AppendLine($"<td>{idleH:F2}</td>");
                sb.AppendLine("</tr>");
            }

            // Fila de totales
            sb.AppendLine("<tr>");
            sb.AppendLine("<td colspan='2'><strong>Totales semana</strong></td>");
            sb.AppendLine($"<td><strong>{totalOnHours:F2}</strong></td>");
            sb.AppendLine($"<td><strong>{totalActiveHours:F2}</strong></td>");
            sb.AppendLine($"<td><strong>{totalIdleHours:F2}</strong></td>");
            sb.AppendLine("</tr>");

            // Fila de promedios diarios (dividimos entre 7 días)
            int daysCount = weekly.Days.Count > 0 ? weekly.Days.Count : 1;
            double avgOn = totalOnHours / daysCount;
            double avgAct = totalActiveHours / daysCount;
            double avgIdle = totalIdleHours / daysCount;

            sb.AppendLine("<tr>");
            sb.AppendLine("<td colspan='2'><strong>Promedio diario</strong></td>");
            sb.AppendLine($"<td><strong>{avgOn:F2}</strong></td>");
            sb.AppendLine($"<td><strong>{avgAct:F2}</strong></td>");
            sb.AppendLine($"<td><strong>{avgIdle:F2}</strong></td>");
            sb.AppendLine("</tr>");

            sb.AppendLine("</table>");

            // ====== Tabla de aplicaciones semanales ======
            sb.AppendLine("<h3>Aplicaciones principales (horas en la semana)</h3>");

            if (weekly.Apps == null || weekly.Apps.Count == 0)
            {
                sb.AppendLine("<p>No hay aplicaciones registradas en esta semana.</p>");
            }
            else
            {
                sb.AppendLine("<table border='1' cellpadding='4' cellspacing='0'>");
                sb.AppendLine("<tr><th>Aplicación (proceso)</th><th>Horas en la semana</th></tr>");

                foreach (var app in weekly.Apps)
                {
                    double hours = app.TotalMinutes / 60.0;
                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td>{app.ProcessName}</td>");
                    sb.AppendLine($"<td>{hours:F2}</td>");
                    sb.AppendLine("</tr>");
                }

                sb.AppendLine("</table>");
            }

            // ====== Tabla de sitios web semanales ======
            sb.AppendLine("<h3>Sitios web principales (horas en la semana)</h3>");

            if (weekly.Sites == null || weekly.Sites.Count == 0)
            {
                sb.AppendLine("<p>No hay sitios web registrados en esta semana.</p>");
            }
            else
            {
                sb.AppendLine("<table border='1' cellpadding='4' cellspacing='0'>");
                sb.AppendLine("<tr><th>Sitio / Dominio</th><th>Horas en la semana</th></tr>");

                foreach (var site in weekly.Sites)
                {
                    double hours = site.TotalMinutes / 60.0;
                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td>{site.Domain}</td>");
                    sb.AppendLine($"<td>{hours:F2}</td>");
                    sb.AppendLine("</tr>");
                }

                sb.AppendLine("</table>");
            }


            sb.AppendLine("<p style='margin-top:20px;font-size:smaller;color:#666;'>");
            sb.AppendLine("Reporte semanal generado automáticamente por ZuriFocus.");
            sb.AppendLine("</p>");

            sb.AppendLine("</body></html>");

            return sb.ToString();
        }

        private static string GetSpanishDayName(DayOfWeek day)
        {
            return day switch
            {
                DayOfWeek.Monday => "Lunes",
                DayOfWeek.Tuesday => "Martes",
                DayOfWeek.Wednesday => "Miércoles",
                DayOfWeek.Thursday => "Jueves",
                DayOfWeek.Friday => "Viernes",
                DayOfWeek.Saturday => "Sábado",
                DayOfWeek.Sunday => "Domingo",
                _ => day.ToString()
            };
        }

        private static void SendEmailReport(WeeklyReportData weekly, EmailSettings emailSettings)
        {
            if (emailSettings.Recipients == null || emailSettings.Recipients.Count == 0)
            {
                Console.WriteLine("Email.Enabled=true pero no hay destinatarios configurados.");
                return;
            }

            string subject = $"Reporte semanal ZuriFocus - {weekly.ComputerId} - " +
                             $"{weekly.WeekStart:yyyy-MM-dd} al {weekly.WeekEnd:yyyy-MM-dd}";

            string bodyHtml = BuildHtmlReportBody(weekly);

            try
            {
                using var message = new MailMessage();
                message.From = new MailAddress(emailSettings.FromAddress, emailSettings.FromName);

                foreach (var to in emailSettings.Recipients)
                {
                    if (!string.IsNullOrWhiteSpace(to))
                        message.To.Add(to);
                }

                message.Subject = subject;
                message.Body = bodyHtml;
                message.IsBodyHtml = true;

                using var client = new SmtpClient(emailSettings.SmtpHost, emailSettings.SmtpPort);
                client.EnableSsl = emailSettings.UseSsl;
                client.Credentials = new NetworkCredential(emailSettings.Username, emailSettings.Password);

                client.Send(message);
                Console.WriteLine($"Correo de reporte semanal enviado para la semana {weekly.WeekStart:yyyy-MM-dd} - {weekly.WeekEnd:yyyy-MM-dd}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al enviar el correo de reporte semanal:");
                Console.WriteLine(ex.Message);
            }
        }


        private static DateTime GetWeekStartMonday(DateTime date)
        {
            // DayOfWeek: Sunday = 0, Monday = 1, ...
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.Date.AddDays(-diff);
        }

        private static WeeklyReportData BuildWeeklyReportData(DateTime weekStart, DateTime weekEnd, string computerId)
        {
            var result = new WeeklyReportData
            {
                WeekStart = weekStart,
                WeekEnd = weekEnd,
                ComputerId = computerId
            };

            DateTime current = weekStart;
            string? user = null;

            var appTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var siteTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            while (current <= weekEnd)
            {
                var log = LoadDayLogFromFile(current, computerId);

                int totalOn = 0;
                int active = 0;
                int idle = 0;

                if (log != null && log.Sessions != null)
                {
                    if (user == null && !string.IsNullOrWhiteSpace(log.WindowsUser))
                        user = log.WindowsUser;

                    foreach (var s in log.Sessions)
                    {
                        totalOn += s.TotalMinutes;
                        active += s.ActiveMinutes;
                        idle += s.IdleMinutes;
                    }
                }

                if (log != null)
                {
                    // Acumular aplicaciones de ese día
                    if (log.Applications != null)
                    {
                        foreach (var app in log.Applications)
                        {
                            if (app.TotalMinutes <= 0 || string.IsNullOrWhiteSpace(app.ProcessName))
                                continue;

                            if (!appTotals.ContainsKey(app.ProcessName))
                                appTotals[app.ProcessName] = 0;

                            appTotals[app.ProcessName] += app.TotalMinutes;
                        }
                    }

                    // Acumular sitios de ese día
                    if (log.Websites != null)
                    {
                        foreach (var site in log.Websites)
                        {
                            if (site.TotalMinutes <= 0 || string.IsNullOrWhiteSpace(site.Domain))
                                continue;

                            if (!siteTotals.ContainsKey(site.Domain))
                                siteTotals[site.Domain] = 0;

                            siteTotals[site.Domain] += site.TotalMinutes;
                        }
                    }
                }


                result.Days.Add(new WeeklyDaySummary
                {
                    Date = current,
                    TotalOnMinutes = totalOn,
                    ActiveMinutes = active,
                    IdleMinutes = idle
                });

                current = current.AddDays(1);
            }

            result.WindowsUser = user ?? "";

            // Convertir diccionarios en listas ordenadas (descendente por minutos)
            result.Apps = appTotals
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new WeeklyAppSummary
                {
                    ProcessName = kv.Key,
                    TotalMinutes = kv.Value
                })
                .ToList();

            result.Sites = siteTotals
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new WeeklySiteSummary
                {
                    Domain = kv.Key,
                    TotalMinutes = kv.Value
                })
                .ToList();

            result.WindowsUser = user ?? "";
            return result;


        }


        private static void MaybeSendWeeklyReport(MonitorSettings settings, string computerId)
        {
            if (settings.Email == null || !settings.Email.Enabled)
            {
                return;
            }

            EmailState state = LoadEmailState();
            DateTime today = DateTime.Today;

            // Semana actual (donde estamos hoy), empezando en lunes
            DateTime currentWeekStart = GetWeekStartMonday(today);

            // Semana anterior: lunes a domingo
            DateTime lastWeekStart = currentWeekStart.AddDays(-7);
            DateTime lastWeekEnd = currentWeekStart.AddDays(-1); // domingo anterior

            // Si ya enviamos un reporte cuyo "fin" es >= al domingo pasado, no hacemos nada
            if (state.LastReportSentDate.HasValue &&
                state.LastReportSentDate.Value >= lastWeekEnd)
            {
                Console.WriteLine("Ya se envió el reporte de la semana anterior.");
                return;
            }

            // Construimos un resumen semanal para esa semana (lunes-domingo)
            WeeklyReportData weekly = BuildWeeklyReportData(lastWeekStart, lastWeekEnd, computerId);

            // ¿hubo algo de uso en toda la semana?
            bool anyUsage = weekly.Days.Exists(d =>
                d.TotalOnMinutes > 0 || d.ActiveMinutes > 0 || d.IdleMinutes > 0);

            if (!anyUsage)
            {
                Console.WriteLine("No hay uso registrado en la semana anterior. No se envía reporte semanal.");
                return;
            }

            // Enviamos el correo
            SendEmailReport(weekly, settings.Email);

            // Guardamos que ya enviamos esta semana
            state.LastReportSentDate = lastWeekEnd;
            SaveEmailState(state);


        }




        private static void MergeApplications(DayLog log, List<AppUsage> newUsages)
        {
            foreach (var newApp in newUsages)
            {
                if (newApp.TotalMinutes <= 0) continue;

                var existing = log.Applications.Find(a =>
                    string.Equals(a.ProcessName, newApp.ProcessName, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    log.Applications.Add(new AppUsage
                    {
                        ProcessName = newApp.ProcessName,
                        TotalMinutes = newApp.TotalMinutes,
                        FirstUse = newApp.FirstUse,
                        LastUse = newApp.LastUse
                    });
                }
                else
                {
                    existing.TotalMinutes += newApp.TotalMinutes;

                    if (newApp.FirstUse.HasValue)
                    {
                        if (!existing.FirstUse.HasValue || newApp.FirstUse < existing.FirstUse)
                            existing.FirstUse = newApp.FirstUse;
                    }

                    if (newApp.LastUse.HasValue)
                    {
                        if (!existing.LastUse.HasValue || newApp.LastUse > existing.LastUse)
                            existing.LastUse = newApp.LastUse;
                    }
                }
            }
        }

        private static void MergeWebsites(DayLog log, List<WebsiteUsage> newUsages)
        {
            foreach (var newSite in newUsages)
            {
                if (newSite.TotalMinutes <= 0) continue;
                if (string.IsNullOrWhiteSpace(newSite.Domain)) continue;

                var existing = log.Websites.Find(w =>
                    string.Equals(w.Domain, newSite.Domain, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    log.Websites.Add(new WebsiteUsage
                    {
                        Domain = newSite.Domain,
                        TotalMinutes = newSite.TotalMinutes,
                        FirstUse = newSite.FirstUse,
                        LastUse = newSite.LastUse
                    });
                }
                else
                {
                    existing.TotalMinutes += newSite.TotalMinutes;

                    if (newSite.FirstUse.HasValue)
                    {
                        if (!existing.FirstUse.HasValue || newSite.FirstUse < existing.FirstUse)
                            existing.FirstUse = newSite.FirstUse;
                    }

                    if (newSite.LastUse.HasValue)
                    {
                        if (!existing.LastUse.HasValue || newSite.LastUse > existing.LastUse)
                            existing.LastUse = newSite.LastUse;
                    }
                }
            }
        }
    }

    // ==================== Modelo de datos ====================

    public class DayLog
    {
        public DateTime Date { get; set; }
        public string ComputerId { get; set; } = "";
        public string WindowsUser { get; set; } = "";
        public List<Session> Sessions { get; set; } = new();
        public List<AppUsage> Applications { get; set; } = new();
        public List<WebsiteUsage> Websites { get; set; } = new();
    }

    public class Session
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public int ActiveMinutes { get; set; }
        public int IdleMinutes { get; set; }

        public int TotalMinutes => (int)(End - Start).TotalMinutes;
    }

    public class AppUsage
    {
        public string ProcessName { get; set; } = "";
        public int TotalMinutes { get; set; }
        public DateTime? FirstUse { get; set; }
        public DateTime? LastUse { get; set; }
    }

    public class WebsiteUsage
    {
        public string Domain { get; set; } = "";
        public int TotalMinutes { get; set; }
        public DateTime? FirstUse { get; set; }
        public DateTime? LastUse { get; set; }
    }

    public class WeeklyDaySummary
    {
        public DateTime Date { get; set; }
        public int TotalOnMinutes { get; set; }
        public int ActiveMinutes { get; set; }
        public int IdleMinutes { get; set; }
    }

    public class WeeklyReportData
    {
        public DateTime WeekStart { get; set; }  // lunes
        public DateTime WeekEnd { get; set; }    // domingo
        public string ComputerId { get; set; } = "";
        public string WindowsUser { get; set; } = "";
        public List<WeeklyDaySummary> Days { get; set; } = new();
        public List<WeeklyAppSummary> Apps { get; set; } = new();
        public List<WeeklySiteSummary> Sites { get; set; } = new();
    }

    public class WeeklyAppSummary
    {
        public string ProcessName { get; set; } = "";
        public int TotalMinutes { get; set; }
    }

    public class WeeklySiteSummary
    {
        public string Domain { get; set; } = "";
        public int TotalMinutes { get; set; }
    }

    // ==================== IdleTracker ====================

    public class IdleTracker
    {
        private readonly int _idleThresholdSeconds;
        private readonly Timer _timer;

        private DateTime _lastTickTime;
        private bool _isCurrentlyIdle;

        public int ActiveSeconds { get; private set; }
        public int IdleSeconds { get; private set; }

        public IdleTracker(int idleThresholdSeconds)
        {
            _idleThresholdSeconds = idleThresholdSeconds;

            _timer = new Timer(1000); // cada 1 segundo
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
        }

        public void Start()
        {
            _lastTickTime = DateTime.Now;
            _isCurrentlyIdle = false;
            ActiveSeconds = 0;
            IdleSeconds = 0;

            _timer.Start();
        }

        public void Stop()
        {
            UpdateElapsed();
            _timer.Stop();
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            UpdateElapsed();
        }

        private void UpdateElapsed()
        {
            DateTime now = DateTime.Now;
            double elapsedSeconds = (now - _lastTickTime).TotalSeconds;
            if (elapsedSeconds < 0) elapsedSeconds = 0;

            bool isIdleNow = IsIdleNow();

            if (_isCurrentlyIdle)
            {
                IdleSeconds += (int)Math.Round(elapsedSeconds);
            }
            else
            {
                ActiveSeconds += (int)Math.Round(elapsedSeconds);
            }

            _isCurrentlyIdle = isIdleNow;
            _lastTickTime = now;
        }

        private bool IsIdleNow()
        {
            uint idleTimeMs = GetIdleTimeMilliseconds();
            return idleTimeMs >= _idleThresholdSeconds * 1000;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private static uint GetIdleTimeMilliseconds()
        {
            LASTINPUTINFO lastInputInfo = new LASTINPUTINFO();
            lastInputInfo.cbSize = (uint)Marshal.SizeOf(lastInputInfo);

            if (!GetLastInputInfo(ref lastInputInfo))
            {
                return 0;
            }

            uint lastInputTick = lastInputInfo.dwTime;
            uint currentTick = (uint)Environment.TickCount;

            return currentTick - lastInputTick;
        }
    }

    // ==================== AppTracker ====================

    public class AppTracker
    {
        private readonly Timer _timer;
        private DateTime _lastTickTime;

        private readonly Dictionary<string, AppUsageAccumulator> _apps = new();

        public AppTracker()
        {
            _timer = new Timer(1000); // cada 1 segundo
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
        }

        public void Start()
        {
            _lastTickTime = DateTime.Now;
            _apps.Clear();
            _timer.Start();
        }

        public void Stop()
        {
            UpdateElapsed();
            _timer.Stop();
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            UpdateElapsed();
        }

        private void UpdateElapsed()
        {
            DateTime now = DateTime.Now;
            double elapsedSeconds = (now - _lastTickTime).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                _lastTickTime = now;
                return;
            }

            string processName = GetActiveProcessName() ?? "Unknown";
            int seconds = (int)Math.Round(elapsedSeconds);

            if (!_apps.TryGetValue(processName, out var acc))
            {
                acc = new AppUsageAccumulator();
                _apps[processName] = acc;
            }

            acc.Seconds += seconds;
            if (!acc.FirstUse.HasValue)
                acc.FirstUse = now;
            acc.LastUse = now;

            _lastTickTime = now;
        }

        public List<AppUsage> GetAppUsageMinutes()
        {
            var result = new List<AppUsage>();

            foreach (var kvp in _apps)
            {
                string processName = kvp.Key;
                var acc = kvp.Value;

                int minutes = (int)Math.Round(acc.Seconds / 60.0);
                if (minutes <= 0) continue;

                result.Add(new AppUsage
                {
                    ProcessName = processName,
                    TotalMinutes = minutes,
                    FirstUse = acc.FirstUse,
                    LastUse = acc.LastUse
                });
            }

            return result;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private static string? GetActiveProcessName()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero)
                    return null;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == 0) return null;

                using Process proc = Process.GetProcessById((int)pid);
                return proc.ProcessName + ".exe";
            }
            catch
            {
                return null;
            }
        }

        private class AppUsageAccumulator
        {
            public int Seconds;
            public DateTime? FirstUse;
            public DateTime? LastUse;
        }
    }

    // ==================== WebsiteTracker ====================

    public class WebsiteTracker
    {
        private readonly Timer _timer;
        private DateTime _lastTickTime;

        private readonly Dictionary<string, WebsiteUsageAccumulator> _sites = new();

        // lista básica de procesos de navegador que queremos considerar
        private static readonly string[] BrowserProcesses = new[]
        {
            "chrome.exe",
            "msedge.exe",
            "firefox.exe",
            "opera.exe",
            "brave.exe"
        };

        public WebsiteTracker()
        {
            _timer = new Timer(1000); // cada 1 segundo
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
        }

        public void Start()
        {
            _lastTickTime = DateTime.Now;
            _sites.Clear();
            _timer.Start();
        }

        public void Stop()
        {
            UpdateElapsed();
            _timer.Stop();
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            UpdateElapsed();
        }

        private void UpdateElapsed()
        {
            DateTime now = DateTime.Now;
            double elapsedSeconds = (now - _lastTickTime).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                _lastTickTime = now;
                return;
            }

            var info = GetActiveBrowserAndSite();
            if (info != null)
            {
                string label = info.Value.SiteLabel;
                int seconds = (int)Math.Round(elapsedSeconds);

                if (!_sites.TryGetValue(label, out var acc))
                {
                    acc = new WebsiteUsageAccumulator();
                    _sites[label] = acc;
                }

                acc.Seconds += seconds;
                if (!acc.FirstUse.HasValue)
                    acc.FirstUse = now;
                acc.LastUse = now;
            }

            _lastTickTime = now;
        }

        public List<WebsiteUsage> GetWebsiteUsageMinutes()
        {
            var result = new List<WebsiteUsage>();

            foreach (var kvp in _sites)
            {
                string label = kvp.Key;
                var acc = kvp.Value;

                int minutes = (int)Math.Round(acc.Seconds / 60.0);
                if (minutes <= 0) continue;

                result.Add(new WebsiteUsage
                {
                    Domain = label, // aquí usamos el "label" (título recortado) como nombre del sitio
                    TotalMinutes = minutes,
                    FirstUse = acc.FirstUse,
                    LastUse = acc.LastUse
                });
            }

            return result;
        }

        // ------- helpers: ventana activa, título, etc. -------

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        private static (string ProcessName, string SiteLabel)? GetActiveBrowserAndSite()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero)
                    return null;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == 0) return null;

                using Process proc = Process.GetProcessById((int)pid);
                string processName = proc.ProcessName.ToLowerInvariant() + ".exe";

                // si no es un navegador, no contamos
                bool isBrowser = false;
                foreach (var b in BrowserProcesses)
                {
                    if (string.Equals(processName, b, StringComparison.OrdinalIgnoreCase))
                    {
                        isBrowser = true;
                        break;
                    }
                }
                if (!isBrowser) return null;

                // leemos el título de la ventana
                StringBuilder sb = new StringBuilder(512);
                int length = GetWindowText(hWnd, sb, sb.Capacity);
                if (length <= 0) return null;

                string title = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(title)) return null;

                // muchos navegadores ponen "Página - Google Chrome" o similar
                // nos quedamos con la parte antes del último " - "
                int dashIndex = title.LastIndexOf(" - ");
                if (dashIndex > 0)
                {
                    title = title.Substring(0, dashIndex).Trim();
                }

                // si quedó muy largo, recortamos un poco para el reporte
                if (title.Length > 60)
                    title = title.Substring(0, 60) + "...";

                return (processName, title);
            }
            catch
            {
                return null;
            }
        }

        private class WebsiteUsageAccumulator
        {
            public int Seconds;
            public DateTime? FirstUse;
            public DateTime? LastUse;
        }
    }
    // ==================== Configuración ====================

    public class MonitorSettings
    {
        public int IdleThresholdSeconds { get; set; } = 60;

        public EmailSettings Email { get; set; } = new EmailSettings();
    }

    public class EmailSettings
    {
        public bool Enabled { get; set; } = false;
        public string SmtpHost { get; set; } = "";
        public int SmtpPort { get; set; } = 587;
        public bool UseSsl { get; set; } = true;
        public string FromAddress { get; set; } = "";
        public string FromName { get; set; } = "ZuriFocus";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public List<string> Recipients { get; set; } = new();
    }

    public class EmailState
    {
        public DateTime? LastReportSentDate { get; set; }
    }


}
