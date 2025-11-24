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

            // 1) Crear el DayLog del día actual con datos reales del equipo
            DayLog todayLog = new DayLog
            {
                Date = DateTime.Today,
                ComputerId = Environment.MachineName,
                WindowsUser = Environment.UserName
            };

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

            // Usamos un cronómetro para medir la duración de la sesión
            Stopwatch stopwatch = Stopwatch.StartNew();

            // Esperamos a que el usuario presione ENTER
            Console.ReadLine();

            stopwatch.Stop();

            // 3) Cerrar sesión: fin = ahora, minutos activos = duración total, idle = 0 (por ahora)
            currentSession.End = DateTime.Now;
            currentSession.ActiveMinutes = (int)Math.Round(stopwatch.Elapsed.TotalMinutes);
            currentSession.IdleMinutes = 0;

            // Agregamos la sesión al DayLog
            todayLog.Sessions.Add(currentSession);

            // 4) Guardar el DayLog en JSON
            SaveDayLog(todayLog);

            Console.WriteLine();
            Console.WriteLine("Sesión registrada:");
            Console.WriteLine($"  Inicio : {currentSession.Start:HH:mm:ss}");
            Console.WriteLine($"  Fin    : {currentSession.End:HH:mm:ss}");
            Console.WriteLine($"  Total  : {currentSession.TotalMinutes} min");
            Console.WriteLine($"  Activo : {currentSession.ActiveMinutes} min");
            Console.WriteLine($"  Idle   : {currentSession.IdleMinutes} min (aún sin calcular)");
            Console.WriteLine();
            Console.WriteLine("Log guardado en la carpeta 'logs'. Presiona ENTER para salir.");
            Console.ReadLine();
        }

        private static void SaveDayLog(DayLog log)
        {
            // Carpeta donde se guardarán los logs, al lado del .exe
            string logsFolder = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsFolder);

            string fileName = $"{log.Date:yyyy-MM-dd}-{log.ComputerId}.json";
            string filePath = Path.Combine(logsFolder, fileName);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true // para que el JSON se vea bonito
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
