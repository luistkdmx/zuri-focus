using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

// Alias para evitar conflictos de Timer
using Timer = System.Timers.Timer;
using ElapsedEventArgs = System.Timers.ElapsedEventArgs;

namespace ZuriFocus.Monitor
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "ZuriFocus Monitor - Sesión con activo/idle y apps";
            Console.WriteLine("ZuriFocus Monitor iniciado...");
            Console.WriteLine();

            DateTime today = DateTime.Today;
            string computerId = Environment.MachineName;
            string windowsUser = Environment.UserName;

            // 1) Cargar el DayLog del día si existe, o crear uno nuevo
            DayLog todayLog = LoadDayLogOrCreate(today, computerId, windowsUser);

            // 2) Crear una sesión que empieza ahora
            Session currentSession = new Session
            {
                Start = DateTime.Now
            };

            Console.WriteLine($"Equipo: {todayLog.ComputerId}");
            Console.WriteLine($"Usuario Windows: {todayLog.WindowsUser}");
            Console.WriteLine($"Inicio de sesión: {currentSession.Start:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine();
            Console.WriteLine("Monitoreando sesión actual...");
            Console.WriteLine(" - Idle: si pasan 60 segundos sin mover mouse/teclado, se cuenta como inactivo.");
            Console.WriteLine(" - Apps: se mide qué proceso está en primer plano (chrome, excel, etc.).");
            Console.WriteLine("Cuando quieras terminar la sesión, presiona ENTER.");
            Console.WriteLine();

            // Cronómetro de duración total de la sesión
            Stopwatch stopwatch = Stopwatch.StartNew();

            // Trackers de actividad / idle y aplicaciones
            IdleTracker idleTracker = new IdleTracker(idleThresholdSeconds: 60);
            AppTracker appTracker = new AppTracker();

            idleTracker.Start();
            appTracker.Start();

            // Esperamos a que el usuario presione ENTER para terminar la sesión
            Console.ReadLine();

            // Paramos todo
            stopwatch.Stop();
            idleTracker.Stop();
            appTracker.Stop();

            currentSession.End = DateTime.Now;

            // Convertimos segundos activos/idle a minutos
            currentSession.ActiveMinutes = (int)Math.Round(idleTracker.ActiveSeconds / 60.0);
            currentSession.IdleMinutes = (int)Math.Round(idleTracker.IdleSeconds / 60.0);

            // 3) Agregar la sesión al DayLog
            todayLog.Sessions.Add(currentSession);

            // 4) Volcar el uso de aplicaciones al DayLog.Applications
            List<AppUsage> appUsages = appTracker.GetAppUsageMinutes();
            MergeApplications(todayLog, appUsages);

            // 5) Guardar el DayLog
            SaveDayLog(todayLog);

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
                    // Si algo falla al leer/deserializar, creamos uno nuevo limpio
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

    // ==================== Tracker de actividad / idle ====================

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
            // Contabilizamos el último tramo de tiempo
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

        // ====== Interop con Windows: GetLastInputInfo ======

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

    // ==================== Tracker de aplicaciones ====================

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

        // ====== Obtener proceso activo ======

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
                // ProcessName devuelve "chrome", "WINWORD", etc.
                // Le agregamos ".exe" para que se vea como en el Administrador de tareas
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
}
