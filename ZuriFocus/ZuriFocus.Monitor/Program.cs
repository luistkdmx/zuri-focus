using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ZuriFocus.Monitor
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "ZuriFocus Monitor - Versión de prueba";
            Console.WriteLine("ZuriFocus Monitor iniciado...");
            Console.WriteLine();

            // Crear un log de ejemplo para probar la estructura
            DayLog todayLog = DayLog.CreateExample();

            // Guardar el log en un archivo JSON
            SaveDayLog(todayLog);

            Console.WriteLine("Log de ejemplo guardado en la carpeta 'logs'.");
            Console.WriteLine("Presiona ENTER para salir.");
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

        // Método estático para generar un ejemplo de prueba
        public static DayLog CreateExample()
        {
            var log = new DayLog
            {
                Date = DateTime.Today,
                ComputerId = "EQUIPO_01",
                WindowsUser = Environment.UserName
            };

            // Sesión de ejemplo
            log.Sessions.Add(new Session
            {
                Start = DateTime.Today.AddHours(8.5),   // 08:30
                End = DateTime.Today.AddHours(13),      // 13:00
                ActiveMinutes = 220,
                IdleMinutes = 50
            });

            // Uso de aplicaciones de ejemplo
            log.Applications.Add(new AppUsage
            {
                ProcessName = "chrome.exe",
                TotalMinutes = 180,
                FirstUse = DateTime.Today.AddHours(9),
                LastUse = DateTime.Today.AddHours(12)
            });

            log.Applications.Add(new AppUsage
            {
                ProcessName = "excel.exe",
                TotalMinutes = 45,
                FirstUse = DateTime.Today.AddHours(10.5),
                LastUse = DateTime.Today.AddHours(11.25)
            });

            // Uso de sitios web de ejemplo
            log.Websites.Add(new WebsiteUsage
            {
                Domain = "chatgpt.com",
                TotalMinutes = 48,
                FirstUse = DateTime.Today.AddHours(9.25),
                LastUse = DateTime.Today.AddHours(11.0)
            });

            log.Websites.Add(new WebsiteUsage
            {
                Domain = "youtube.com",
                TotalMinutes = 30,
                FirstUse = DateTime.Today.AddHours(12),
                LastUse = DateTime.Today.AddHours(13)
            });

            return log;
        }
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
