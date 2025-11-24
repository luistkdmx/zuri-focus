using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ZuriFocus.Monitor
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "ZuriFocus Monitor - Sesión simple";
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
            Console.WriteLine("Cuando quieras terminar la sesión, presiona ENTER.");
            Console.WriteLine();

            Stopwatch stopwatch = Stopwatch.StartNew();

            Console.ReadLine();

            stopwatch.Stop();

            currentSession.End = DateTime.Now;
            currentSession.ActiveMinutes = (int)Math.Round(stopwatch.Elapsed.TotalMinutes);
            currentSession.IdleMinutes = 0; // luego lo calcularemos de verdad

            // 3) Agregar la sesión al DayLog y guardar
            todayLog.Sessions.Add(currentSession);
            SaveDayLog(todayLog);

            Console.WriteLine();
            Console.WriteLine("Sesión registrada:");
            Console.WriteLine($"  Inicio : {currentSession.Start:HH:mm:ss}");
            Console.WriteLine($"  Fin    : {currentSession.End:HH:mm:ss}");
            Console.WriteLine($"  Total  : {currentSession.TotalMinutes} min");
            Console.WriteLine($"  Activo : {currentSession.ActiveMinutes} min");
            Console.WriteLine($"  Idle   : {currentSession.IdleMinutes} min (aún sin calcular)");
            Console.WriteLine();
            Console.WriteLine("Log actualizado en la carpeta 'logs'. Presiona ENTER para salir.");
            Console.ReadLine();
        }

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
                        // Por si el usuario de Windows cambió, lo actualizamos
                        existing.WindowsUser = windowsUser;
                        return existing;
                    }
                }
                catch
                {
                    // Si algo falla al leer/deserializar, creamos uno nuevo limpio
                }
            }

            // No hay archivo o no se pudo leer, creamos un DayLog nuevo
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
    }

    // ======== CLASES DEL MODELO DE DATOS ========

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
}
