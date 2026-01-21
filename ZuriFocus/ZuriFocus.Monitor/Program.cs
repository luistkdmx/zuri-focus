using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Mail;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

// Alias para evitar conflictos de Timer
using Timer = System.Timers.Timer;
using ElapsedEventArgs = System.Timers.ElapsedEventArgs;

namespace ZuriFocus.Monitor
{
    internal class Program
    {
        private static DayLog? _currentDayLog;
        private static Session? _currentSession;
        private static IdleTracker? _idleTracker;
        private static AppTracker? _appTracker;
        private static WebsiteTracker? _websiteTracker;

        // Timer para autosave
        private static Timer? _saveTimer;

        // Para no duplicar minutos al guardar muchas veces
        private static readonly Dictionary<string, int> _lastSavedAppMinutes = new();
        private static readonly Dictionary<string, int> _lastSavedWebsiteMinutes = new();

        // Configuración y datos del equipo (para usar en cualquier parte)
        private static MonitorSettings? _settings;
        private static string _computerId = "";
        private static string _windowsUser = "";

        static void Main(string[] args)
        {
            // 1) Log rápido de arranque
            try
            {
                var dataRoot = GetDataRootDirectory();
                var logPath = Path.Combine(dataRoot, "monitor_startup.log");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Monitor arrancó{Environment.NewLine}");
            }
            catch
            {
                // No queremos que falle por esto
            }

            DateTime today = DateTime.Today;
            string computerId = Environment.MachineName;
            string windowsUser = Environment.UserName;

            // 2) Cargar settings (idle, correo, etc.)
            MonitorSettings settings = LoadSettings();

            // Guardamos en campos estáticos para usarlos en otras funciones
            _settings = settings;
            _computerId = computerId;
            _windowsUser = windowsUser;

            MaybeSendInstallNotification(settings, computerId);

            // 3) Intentar enviar reporte DIARIO del día anterior
            MaybeSendDailyReport(today);

            // 4) Cargar o crear DayLog de hoy
            _currentDayLog = LoadDayLogOrCreate(today, computerId, windowsUser);

            // 5) Crear sesión de esta ejecución (login actual)
            _currentSession = new Session
            {
                Start = DateTime.Now
            };
            _currentDayLog.Sessions.Add(_currentSession);

            // 6) Crear trackers
            _idleTracker = new IdleTracker(settings.IdleThresholdSeconds);
            _appTracker = new AppTracker();
            _websiteTracker = new WebsiteTracker();

            _idleTracker.Start();
            _appTracker.Start();
            _websiteTracker.Start();

            // 7) Hacer un primer guardado inmediato para que exista ya el JSON
            UpdateAndSaveNow();

            // 8) Configurar timer para autosave cada 60 s
            _saveTimer = new Timer(60_000); // 60,000 ms = 1 minuto
            _saveTimer.AutoReset = true;
            _saveTimer.Elapsed += (s, e) => UpdateAndSaveNow();
            _saveTimer.Start();

            // 9) Mantener el proceso vivo "en segundo plano"
            Thread.Sleep(Timeout.Infinite);
        }

        private static void MaybeSendInstallNotification(MonitorSettings settings, string computerId)
        {
            if (settings.Email == null || !settings.Email.Enabled)
                return;

            if (settings.Email.Recipients == null || settings.Email.Recipients.Count == 0)
                return;

            EmailState state = LoadEmailState();

            // Si ya se envió una vez, no volver a enviar
            if (state.InstallNotificationDate.HasValue)
            {
                Console.WriteLine("La notificación de instalación ya fue enviada anteriormente.");
                return;
            }

            string subject = $"ZuriFocus instalado en {computerId}";

            string bodyHtml = $@"
<html>
<body>
    <h2>ZuriFocus instalado</h2>
    <p>El sistema <strong>ZuriFocus</strong> ha sido instalado en el equipo: <strong>{computerId}</strong>.</p>
    <p>A partir de mañana comenzará a recibir los reportes de uso diario de este equipo.</p>
    <p style='margin-top:20px;font-size:smaller;color:#666;'>
        Mensaje automático generado por ZuriFocus.
    </p>
</body>
</html>";

            try
            {
                using var message = new MailMessage();
                message.From = new MailAddress(settings.Email.FromAddress, settings.Email.FromName);

                foreach (var to in settings.Email.Recipients)
                {
                    if (!string.IsNullOrWhiteSpace(to))
                        message.To.Add(to);
                }

                message.Subject = subject;
                message.Body = bodyHtml;
                message.IsBodyHtml = true;

                using var client = new SmtpClient(settings.Email.SmtpHost, settings.Email.SmtpPort);
                client.EnableSsl = settings.Email.UseSsl;
                client.Credentials = new NetworkCredential(settings.Email.Username, settings.Email.Password);

                client.Send(message);
                Console.WriteLine($"Correo de instalación enviado para el equipo {computerId}.");

                // Marcamos como enviado
                state.InstallNotificationDate = DateTime.Now;
                SaveEmailState(state);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al enviar el correo de instalación:");
                Console.WriteLine(ex.Message);
            }
        }


        private static void MaybeSendDailyReport(DateTime today)
        {
            // Necesitamos settings y computerId inicializados
            if (_settings == null || _settings.Email == null || !_settings.Email.Enabled)
                return;

            if (_settings.Email.Recipients == null || _settings.Email.Recipients.Count == 0)
                return;

            if (string.IsNullOrWhiteSpace(_computerId))
                _computerId = Environment.MachineName;

            EmailState state = LoadEmailState();

            // Día a reportar: AYER respecto a "today"
            DateTime targetDate = today.Date.AddDays(-1);

            // Si ya enviamos un reporte para ese día (o uno posterior), no repetimos
            if (state.LastReportSentDate.HasValue &&
                state.LastReportSentDate.Value.Date >= targetDate)
            {
                Console.WriteLine($"Ya se envió el reporte para {targetDate:yyyy-MM-dd}.");
                return;
            }

            // Cargar el log del día a reportar
            var log = LoadDayLogFromFile(targetDate, _computerId);
            if (log == null)
            {
                Console.WriteLine($"No hay log para el día {targetDate:yyyy-MM-dd}. No se envía reporte diario.");
                return;
            }

            // Ver si hubo algo de uso
            bool anyUsage = false;

            if (log.Sessions != null && log.Sessions.Count > 0)
            {
                foreach (var s in log.Sessions)
                {
                    if (s.TotalMinutes > 0 || s.ActiveMinutes > 0 || s.IdleMinutes > 0)
                    {
                        anyUsage = true;
                        break;
                    }
                }
            }

            if (!anyUsage && log.Applications != null && log.Applications.Count > 0)
            {
                anyUsage = log.Applications.Any(a => a.TotalMinutes > 0);
            }

            if (!anyUsage && log.Websites != null && log.Websites.Count > 0)
            {
                anyUsage = log.Websites.Any(w => w.TotalMinutes > 0);
            }

            if (!anyUsage)
            {
                Console.WriteLine($"No hay uso registrado el día {targetDate:yyyy-MM-dd}. No se envía reporte diario.");
                return;
            }

            // Enviar correo del día
            SendDailyReport(log, _settings.Email);

            // Guardar que ya lo mandamos
            state.LastReportSentDate = targetDate;
            SaveEmailState(state);
        }



        private static void UpdateAndSaveNow()
        {
            if (_currentDayLog == null || _currentSession == null ||
                _idleTracker == null || _appTracker == null || _websiteTracker == null)
                return;

            try
            {
                DateTime now = DateTime.Now;

                // 🔹 0) Detectar cambio de día (por ejemplo, cruzar de 10 a 11 de diciembre)
                if (now.Date != _currentDayLog.Date.Date)
                {
                    HandleDayChange(now);
                }

                // 🔹 1) Actualizar la sesión actual con activo/idle
                _currentSession.End = now;
                _currentSession.ActiveMinutes = (int)Math.Round(_idleTracker.ActiveSeconds / 60.0);
                _currentSession.IdleMinutes = (int)Math.Round(_idleTracker.IdleSeconds / 60.0);

                // 🔹 2) Obtener uso TOTAL de apps desde que se inició esta ejecución
                var allApps = _appTracker.GetAppUsageMinutes();
                var deltaApps = new List<AppUsage>();

                foreach (var app in allApps)
                {
                    int last = 0;
                    _lastSavedAppMinutes.TryGetValue(app.ProcessName, out last);

                    int delta = app.TotalMinutes - last;
                    if (delta <= 0) continue; // nada nuevo

                    deltaApps.Add(new AppUsage
                    {
                        ProcessName = app.ProcessName,
                        TotalMinutes = delta,
                        FirstUse = app.FirstUse,
                        LastUse = app.LastUse
                    });

                    _lastSavedAppMinutes[app.ProcessName] = app.TotalMinutes;
                }

                MergeApplications(_currentDayLog, deltaApps);

                // 🔹 3) Sitios web (dominios) - mismo patrón de delta
                var allSites = _websiteTracker.GetWebsiteUsageMinutes();
                var deltaSites = new List<WebsiteUsage>();

                foreach (var site in allSites)
                {
                    if (string.IsNullOrWhiteSpace(site.Domain)) continue;

                    int last = 0;
                    _lastSavedWebsiteMinutes.TryGetValue(site.Domain, out last);
                    int delta = site.TotalMinutes - last;
                    if (delta <= 0) continue;

                    deltaSites.Add(new WebsiteUsage
                    {
                        Domain = site.Domain,
                        SampleTitle = site.SampleTitle,
                        TotalMinutes = delta,
                        FirstUse = site.FirstUse,
                        LastUse = site.LastUse
                    });

                    _lastSavedWebsiteMinutes[site.Domain] = site.TotalMinutes;
                }

                MergeWebsites(_currentDayLog, deltaSites);

                // 🔹 4) Guardar DayLog actual (sobrescribe el archivo del día)
                SaveDayLog(_currentDayLog);
            }
            catch
            {
                // Para V1, si algo falla en el autosave no queremos que se caiga el proceso.
            }
        }

        /// <summary>
        /// Se llama cuando detectamos que la fecha actual (now.Date)
        /// ya no es igual a _currentDayLog.Date.
        /// Cierra el día anterior y prepara un DayLog nuevo para el día actual.
        /// </summary>
        private static void HandleDayChange(DateTime now)
        {
            if (_currentDayLog == null || _currentSession == null ||
                _idleTracker == null || _appTracker == null || _websiteTracker == null)
                return;

            // 1) Guardar el DayLog del día anterior tal como esté
            SaveDayLog(_currentDayLog);

            // 2) Enviar el reporte diario del día que acaba de terminar.
            //    Hoy es now.Date, así que el "día anterior" es el log que acabamos de cerrar.
            MaybeSendDailyReport(now.Date);

            // 3) Reiniciar trackers para el nuevo día
            _idleTracker.Reset();
            _appTracker.Reset();
            _websiteTracker.Reset();

            _lastSavedAppMinutes.Clear();
            _lastSavedWebsiteMinutes.Clear();

            // 4) Crear un nuevo DayLog para la fecha actual
            DateTime today = now.Date;

            // Usamos los datos ya guardados en estáticos si existen
            string computerId = string.IsNullOrWhiteSpace(_computerId)
                ? Environment.MachineName
                : _computerId;

            string windowsUser = string.IsNullOrWhiteSpace(_windowsUser)
                ? Environment.UserName
                : _windowsUser;

            _currentDayLog = LoadDayLogOrCreate(today, computerId, windowsUser);

            // 5) Nueva sesión a partir de este momento
            _currentSession = new Session
            {
                Start = now
            };
            _currentDayLog.Sessions.Add(_currentSession);
        }




        private static string GetDataRootDirectory()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ZuriFocus");

            Directory.CreateDirectory(dir);
            return dir;
        }



        // ==================== Manejo de DayLog ====================

        private static string GetLogFilePath(DateTime date, string computerId)
        {
            string logsFolder = Path.Combine(GetDataRootDirectory(), "logs");
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
            string dataRoot = GetDataRootDirectory();
            string settingsPath = Path.Combine(dataRoot, "settings.json");

            // Si NO existe en ProgramData, intentamos copiar el que está junto al exe
            if (!File.Exists(settingsPath))
            {
                string exeDir = AppContext.BaseDirectory;
                string initialSettings = Path.Combine(exeDir, "settings.json");

                if (File.Exists(initialSettings))
                {
                    Directory.CreateDirectory(dataRoot);
                    File.Copy(initialSettings, settingsPath, overwrite: false);
                }
            }

            // Si sigue sin existir, creamos uno por defecto
            if (!File.Exists(settingsPath))
            {
                var defaultSettings = new MonitorSettings
                {
                    IdleThresholdSeconds = 60,
                    Email = new EmailSettings
                    {
                        Enabled = true,
                        SmtpHost = "mail.grupozuri.mx",
                        SmtpPort = 587,
                        UseSsl = false,
                        FromAddress = "operaciones@grupozuri.mx",
                        FromName = "ZuriFocus Reporte",
                        Username = "operaciones@grupozuri.mx",
                        Password = "r2ASzTztjo",
                        Recipients = new List<string>()
                    }
                };

                string jsonDefault = JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(settingsPath, jsonDefault);
                return defaultSettings;
            }

            // Leer el settings real
            string json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<MonitorSettings>(json);
            return settings ?? new MonitorSettings();
        }


        private static EmailState LoadEmailState()
        {
            string path = Path.Combine(GetDataRootDirectory(), "email_state.json");
            if (!File.Exists(path))
                return new EmailState();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<EmailState>(json) ?? new EmailState();
        }

        private static void SaveEmailState(EmailState state)
        {
            string path = Path.Combine(GetDataRootDirectory(), "email_state.json");
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
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

        private static string BuildDailyHtmlReportBody(DayLog log)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<html><body>");
            sb.AppendLine($"<h2>Reporte diario ZuriFocus - {log.ComputerId}</h2>");
            sb.AppendLine($"<p><strong>Fecha:</strong> {log.Date:yyyy-MM-dd}</p>");

            if (!string.IsNullOrWhiteSpace(log.WindowsUser))
            {
                sb.AppendLine($"<p><strong>Usuario:</strong> {log.WindowsUser}</p>");
            }

            // ====== Sesiones del día ======
            sb.AppendLine("<h3>Sesiones del día</h3>");

            if (log.Sessions == null || log.Sessions.Count == 0)
            {
                sb.AppendLine("<p>No hay sesiones registradas en este día.</p>");
            }
            else
            {
                sb.AppendLine("<table border='1' cellpadding='4' cellspacing='0'>");
                sb.AppendLine("<tr><th>#</th><th>Inicio</th><th>Fin</th><th>Encendido</th><th>Activo</th><th>Idle</th></tr>");

                int index = 1;
                int totalOn = 0;
                int totalActive = 0;
                int totalIdle = 0;

                foreach (var s in log.Sessions.OrderBy(s => s.Start))
                {
                    int sessionOn = s.TotalMinutes;
                    int sessionActive = s.ActiveMinutes;
                    int sessionIdle = s.IdleMinutes;

                    totalOn += sessionOn;
                    totalActive += sessionActive;
                    totalIdle += sessionIdle;

                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td>{index}</td>");
                    sb.AppendLine($"<td>{s.Start:dd/MM HH:mm}</td>");
                    sb.AppendLine($"<td>{s.End:dd/MM HH:mm}</td>");
                    sb.AppendLine($"<td>{FormatMinutesAsHoursAndMinutes(sessionOn)}</td>");
                    sb.AppendLine($"<td>{FormatMinutesAsHoursAndMinutes(sessionActive)}</td>");
                    sb.AppendLine($"<td>{FormatMinutesAsHoursAndMinutes(sessionIdle)}</td>");
                    sb.AppendLine("</tr>");

                    index++;
                }

                // Totales del día
                sb.AppendLine("<tr>");
                sb.AppendLine("<td colspan='3'><strong>Totales del día</strong></td>");
                sb.AppendLine($"<td><strong>{FormatMinutesAsHoursAndMinutes(totalOn)}</strong></td>");
                sb.AppendLine($"<td><strong>{FormatMinutesAsHoursAndMinutes(totalActive)}</strong></td>");
                sb.AppendLine($"<td><strong>{FormatMinutesAsHoursAndMinutes(totalIdle)}</strong></td>");
                sb.AppendLine("</tr>");

                sb.AppendLine("</table>");
            }

            // ====== Aplicaciones del día ======
            sb.AppendLine("<h3>Aplicaciones del día</h3>");

            if (log.Applications == null || log.Applications.Count == 0)
            {
                sb.AppendLine("<p>No hay aplicaciones registradas en este día.</p>");
            }
            else
            {
                sb.AppendLine("<table border='1' cellpadding='4' cellspacing='0'>");
                sb.AppendLine("<tr><th>Aplicación (proceso)</th><th>Tiempo</th></tr>");

                foreach (var app in log.Applications
                                       .OrderByDescending(a => a.TotalMinutes))
                {
                    if (app.TotalMinutes <= 0) continue;

                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td>{app.ProcessName}</td>");
                    sb.AppendLine($"<td>{FormatMinutesAsHoursAndMinutes(app.TotalMinutes)}</td>");
                    sb.AppendLine("</tr>");
                }

                sb.AppendLine("</table>");
            }

            // ====== Sitios web del día ======
            sb.AppendLine("<h3>Sitios web del día</h3>");

            if (log.Websites == null || log.Websites.Count == 0)
            {
                sb.AppendLine("<p>No hay sitios web registrados en este día.</p>");
            }
            else
            {
                sb.AppendLine("<table border='1' cellpadding='4' cellspacing='0'>");
                sb.AppendLine("<tr><th>Sitio / Dominio</th><th>Tiempo</th></tr>");

                foreach (var site in log.Websites
                                        .OrderByDescending(s => s.TotalMinutes))
                {
                    if (site.TotalMinutes <= 0) continue;

                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td>{site.Domain}</td>");
                    sb.AppendLine($"<td>{FormatMinutesAsHoursAndMinutes(site.TotalMinutes)}</td>");
                    sb.AppendLine("</tr>");
                }

                sb.AppendLine("</table>");
            }

            sb.AppendLine("<p style='margin-top:20px;font-size:smaller;color:#666;'>");
            sb.AppendLine("Reporte diario generado automáticamente por ZuriFocus.");
            sb.AppendLine("</p>");

            sb.AppendLine("</body></html>");

            return sb.ToString();
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

                string onText = FormatMinutesAsHoursAndMinutes(d.TotalOnMinutes);
                string actText = FormatMinutesAsHoursAndMinutes(d.ActiveMinutes);
                string idleText = FormatMinutesAsHoursAndMinutes(d.IdleMinutes);

                string dayName = GetSpanishDayName(d.Date.DayOfWeek);

                sb.AppendLine("<tr>");
                sb.AppendLine($"<td>{i + 1}</td>");
                sb.AppendLine($"<td>{dayName} {d.Date:dd/MM}</td>");
                sb.AppendLine($"<td>{onText}</td>");
                sb.AppendLine($"<td>{actText}</td>");
                sb.AppendLine($"<td>{idleText}</td>");
                sb.AppendLine("</tr>");
            }

            int totalOnMinutes = (int)Math.Round(totalOnHours * 60);
            int totalActiveMinutes = (int)Math.Round(totalActiveHours * 60);
            int totalIdleMinutes = (int)Math.Round(totalIdleHours * 60);


            // Fila de totales
            sb.AppendLine("<tr>");
            sb.AppendLine("<td colspan='2'><strong>Totales semana</strong></td>");
            sb.AppendLine($"<td><strong>{FormatMinutesAsHoursAndMinutes(totalOnMinutes)}</strong></td>");
            sb.AppendLine($"<td><strong>{FormatMinutesAsHoursAndMinutes(totalActiveMinutes)}</strong></td>");
            sb.AppendLine($"<td><strong>{FormatMinutesAsHoursAndMinutes(totalIdleMinutes)}</strong></td>");
            sb.AppendLine("</tr>");


            // Fila de promedios diarios (dividimos entre 7 días)
            int daysCount = weekly.Days.Count > 0 ? weekly.Days.Count : 1;
            int avgOnMinutes = (int)Math.Round(totalOnMinutes / (double)daysCount);
            int avgActMinutes = (int)Math.Round(totalActiveMinutes / (double)daysCount);
            int avgIdleMinutes = (int)Math.Round(totalIdleMinutes / (double)daysCount);

            sb.AppendLine("<tr>");
            sb.AppendLine("<td colspan='2'><strong>Promedio diario</strong></td>");
            sb.AppendLine($"<td><strong>{FormatMinutesAsHoursAndMinutes(avgOnMinutes)}</strong></td>");
            sb.AppendLine($"<td><strong>{FormatMinutesAsHoursAndMinutes(avgActMinutes)}</strong></td>");
            sb.AppendLine($"<td><strong>{FormatMinutesAsHoursAndMinutes(avgIdleMinutes)}</strong></td>");
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
                    sb.AppendLine($"<td>{FormatMinutesAsHoursAndMinutes(app.TotalMinutes)}</td>");
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
                    sb.AppendLine($"<td>{FormatMinutesAsHoursAndMinutes(site.TotalMinutes)}</td>");
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

        private static string FormatMinutesAsHoursAndMinutes(int totalMinutes)
        {
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            return $"{hours:D2}:{minutes:D2}";
        }

        private static string FormatHours(double hours)
        {
            // Convertimos de horas decimales a minutos enteros
            int totalMinutes = (int)Math.Round(hours * 60);
            return FormatMinutesAsHoursAndMinutes(totalMinutes);
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

        private static void SendDailyReport(DayLog log, EmailSettings emailSettings)
        {
            if (emailSettings.Recipients == null || emailSettings.Recipients.Count == 0)
            {
                Console.WriteLine("Email.Enabled=true pero no hay destinatarios configurados.");
                return;
            }

            string subject = $"Reporte diario ZuriFocus - {log.ComputerId} - {log.Date:yyyy-MM-dd}";
            string bodyHtml = BuildDailyHtmlReportBody(log);

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
                Console.WriteLine($"Correo de reporte diario enviado para el día {log.Date:yyyy-MM-dd}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al enviar el correo diario:");
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
                        SampleTitle = newSite.SampleTitle ?? "",
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

                    // actualizar SampleTitle si el nuevo trae algo (puedes decidir si prefieres mantener el primero)
                    if (!string.IsNullOrWhiteSpace(newSite.SampleTitle))
                    {
                        existing.SampleTitle = newSite.SampleTitle;
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
        // Dominio normalizado, p.ej. "netflix.com", "gmail.com"
        public string Domain { get; set; } = "";

        // Un título representativo de pestaña donde se vio este dominio
        // p.ej. "Netflix – Google Chrome", "Bandeja de entrada – Gmail"
        public string SampleTitle { get; set; } = "";

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

        public void Reset()
        {
            ActiveSeconds = 0;
            IdleSeconds = 0;
            _isCurrentlyIdle = false;
            _lastTickTime = DateTime.Now;
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

            // 🔹 Si el salto es muy grande, asumimos suspensión / hibernación / app pausada
            //    y NO lo contamos ni como activo ni como idle.
            if (elapsedSeconds > 300) // 5 minutos; puedes ajustar este umbral
            {
                _lastTickTime = now;
                return;
            }

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

        public void Reset()
        {
            _apps.Clear();
            _lastTickTime = DateTime.Now;
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

            // 🔹 Ignorar saltos muy grandes (suspensión / hibernación)
            if (elapsedSeconds > 300) // 5 minutos
            {
                _lastTickTime = now;
                return;
            }

            string processName = GetActiveProcessName() ?? "Unknown";

            // 🔹 No queremos contar LockApp.exe como "aplicación de trabajo"
            if (string.Equals(processName, "LockApp.exe", StringComparison.OrdinalIgnoreCase))
            {
                _lastTickTime = now;
                return;
            }

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

        public void Reset()
        {
            _sites.Clear();
            _lastTickTime = DateTime.Now;
        }


        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            UpdateElapsed();
        }

        // Mapa básico de palabras clave en el título → dominio
        private static readonly Dictionary<string, string> TitleKeywordDomainMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
        { "YouTube", "youtube.com" },
        { "Gmail", "gmail.com" },
        { "Google Drive", "drive.google.com" },
        { "Google Docs", "docs.google.com" },
        { "Google Sheets", "sheets.google.com" },
        { "Google Meet", "meet.google.com" },
        { "Netflix", "netflix.com" },
        { "WhatsApp", "web.whatsapp.com" },
        { "Facebook", "facebook.com" },
        { "Instagram", "instagram.com" },
        { "Twitter", "twitter.com" },
        { "X (formerly Twitter)", "x.com" },
        { "ChatGPT", "chat.openai.com" },
        { "Canva", "canva.com" },
        { "Amazon", "amazon.com" },
        { "SigmaCap", "sigmacap.mx" },
        { "Grupozuri", "grupozuri.mx" },
        { "Kazáh", "kazah.mx" },
        { "Sigmaplus", "sigmaplus.mx" }
                // luego podemos agregar más según lo que observes en producción
            };

        private static readonly Regex DomainRegex =
            new Regex(@"\b([a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}\b",
                      RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static string NormalizeDomain(string processName, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "Otros";

            // 1) Intentar encontrar un dominio "real" en el título (algo.com, algo.com.mx, etc.)
            var match = DomainRegex.Match(title);
            if (match.Success)
            {
                string rawDomain = match.Value.ToLowerInvariant();
                return SimplifyDomain(rawDomain);
            }

            // 2) Buscar en mapa de palabras clave
            foreach (var kvp in TitleKeywordDomainMap)
            {
                if (title.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return kvp.Value;
                }
            }

            // 3) Como último recurso, agrupar como "Otros (chrome)", "Otros (edge)", etc.
            string shortProcess = processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            return $"Otros ({shortProcess})";
        }

        private static string SimplifyDomain(string domain)
        {
            // mail.google.com -> google.com
            // algo.algo.com.mx -> algo.com.mx
            var parts = domain.Split('.');
            if (parts.Length <= 2)
                return domain;

            string last = parts[^1];
            string secondLast = parts[^2];

            string[] countryTlds = { "mx", "ar", "br", "cl", "co", "uk", "es", "us" };

            if (countryTlds.Contains(last) && (secondLast == "com" || secondLast == "org" || secondLast == "net"))
            {
                // tomamos últimos 3: "algo.com.mx"
                return string.Join(".", parts.Skip(parts.Length - 3));
            }

            // en los demás casos, últimos 2: "google.com"
            return string.Join(".", parts.Skip(parts.Length - 2));
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

            // 🔹 Ignorar saltos muy grandes (suspensión / hibernación)
            if (elapsedSeconds > 300) // 5 minutos
            {
                _lastTickTime = now;
                return;
            }

            var info = GetActiveBrowserAndSite();
            if (info != null)
            {
                string processName = info.Value.ProcessName;
                string fullTitle = info.Value.SiteLabel;

                // Normalizamos a dominio agrupable: "netflix.com", "gmail.com", etc.
                string domain = NormalizeDomain(processName, fullTitle);

                int seconds = (int)Math.Round(elapsedSeconds);

                if (!_sites.TryGetValue(domain, out var acc))
                {
                    acc = new WebsiteUsageAccumulator();
                    _sites[domain] = acc;
                }

                acc.Seconds += seconds;

                if (!acc.FirstUse.HasValue)
                    acc.FirstUse = now;
                acc.LastUse = now;

                // Guardamos algún título representativo (podemos ir actualizando)
                if (string.IsNullOrWhiteSpace(acc.SampleTitle))
                {
                    acc.SampleTitle = fullTitle;
                }
                else
                {
                    acc.SampleTitle = fullTitle;
                }
            }

            _lastTickTime = now;
        }


        public List<WebsiteUsage> GetWebsiteUsageMinutes()
        {
            var result = new List<WebsiteUsage>();

            foreach (var kvp in _sites)
            {
                string domain = kvp.Key;
                var acc = kvp.Value;

                int minutes = (int)Math.Round(acc.Seconds / 60.0);
                if (minutes <= 0) continue;

                result.Add(new WebsiteUsage
                {
                    Domain = domain,
                    SampleTitle = acc.SampleTitle ?? "",
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

                StringBuilder sb = new StringBuilder(512);
                int length = GetWindowText(hWnd, sb, sb.Capacity);
                if (length <= 0) return null;

                string title = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(title)) return null;

                if (title.Length > 200)
                    title = title.Substring(0, 200);

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

            // guardamos algún título completo visto para este dominio
            public string SampleTitle { get; set; } = "";
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

        // Nueva bandera: si ya se envió el correo de "ZuriFocus instalado"
        public DateTime? InstallNotificationDate { get; set; }
    }



}
