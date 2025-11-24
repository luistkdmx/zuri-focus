using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZuriFocus.Reporter
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "ZuriFocus Reporter";
            Console.WriteLine("=== ZuriFocus Reporter ===");
            Console.WriteLine();

            // 1) Intentar encontrar automáticamente la carpeta de logs del Monitor
            string logsFolder = GetDefaultLogsFolder();
            Console.WriteLine($"Carpeta de logs detectada: {logsFolder}");

            if (!Directory.Exists(logsFolder))
            {
                Console.WriteLine("No se encontró la carpeta de logs.");
                Console.WriteLine("Escribe la ruta completa de la carpeta donde están los .json:");
                logsFolder = Console.ReadLine() ?? "";

                if (!Directory.Exists(logsFolder))
                {
                    Console.WriteLine("La carpeta sigue sin existir. Saliendo...");
                    Console.ReadLine();
                    return;
                }
            }

            // 2) Listar archivos .json disponibles
            string[] files = Directory.GetFiles(logsFolder, "*.json");
            if (files.Length == 0)
            {
                Console.WriteLine("No se encontraron archivos .json en la carpeta de logs.");
                Console.ReadLine();
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Archivos de log disponibles:");
            for (int i = 0; i < files.Length; i++)
            {
                Console.WriteLine($"{i + 1}. {Path.GetFileName(files[i])}");
            }

            Console.WriteLine();
            Console.Write("Selecciona un archivo (número): ");
            if (!int.TryParse(Console.ReadLine(), out int index) ||
                index < 1 || index > files.Length)
            {
                Console.WriteLine("Selección inválida. Saliendo...");
                Console.ReadLine();
                return;
            }

            string selectedFile = files[index - 1];
            Console.WriteLine();
            Console.WriteLine($"Leyendo: {selectedFile}");
            Console.WriteLine();

            // 3) Leer y deserializar el JSON
            try
            {
                string json = File.ReadAllText(selectedFile);
                var log = JsonSerializer.Deserialize<DayLog>(json);

                if (log == null)
                {
                    Console.WriteLine("No se pudo deserializar el archivo.");
                }
                else
                {
                    PrintSummary(log);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al leer el archivo:");
                Console.WriteLine(ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("Presiona ENTER para salir...");
            Console.ReadLine();
        }

        // ------------- Helpers -------------

        // En desarrollo, buscamos la carpeta de logs relativa a la solución:
        // ZuriFocus.Reporter\bin\Debug\net8.0\  ->  subimos 4 niveles hasta ZuriFocus\
        // y, desde ahí, bajamos a ZuriFocus.Monitor\bin\Debug\net10.0\logs
        private static string GetDefaultLogsFolder()
        {
            string baseDir = AppContext.BaseDirectory;
            string solutionRoot = Path.GetFullPath(
                Path.Combine(baseDir, "..", "..", "..", ".."));

            string logsFromMonitor = Path.Combine(
                solutionRoot,
                "ZuriFocus.Monitor",
                "bin",
                "Debug",
                "net10.0",
                "logs");

            return logsFromMonitor;
        }

        private static void PrintSummary(DayLog log)
        {
            Console.WriteLine($"Fecha       : {log.Date:yyyy-MM-dd}");
            Console.WriteLine($"Equipo      : {log.ComputerId}");
            Console.WriteLine($"Usuario     : {log.WindowsUser}");
            Console.WriteLine();

            PrintSessions(log);
            PrintApplications(log);
            PrintWebsites(log);
        }

        private static void PrintSessions(DayLog log)
        {
            Console.WriteLine("== Sesiones ==");
            if (log.Sessions == null || log.Sessions.Count == 0)
            {
                Console.WriteLine("No hay sesiones registradas.");
                Console.WriteLine();
                return;
            }

            int i = 1;
            foreach (var s in log.Sessions)
            {
                Console.WriteLine($"Sesión {i++}:");
                Console.WriteLine($"  Inicio : {s.Start:HH:mm:ss}");
                Console.WriteLine($"  Fin    : {s.End:HH:mm:ss}");
                Console.WriteLine($"  Total  : {s.TotalMinutes} min");
                Console.WriteLine($"  Activo : {s.ActiveMinutes} min");
                Console.WriteLine($"  Idle   : {s.IdleMinutes} min");
                Console.WriteLine();
            }
        }

        private static void PrintApplications(DayLog log)
        {
            Console.WriteLine("== Aplicaciones (ordenadas por tiempo) ==");

            if (log.Applications == null || log.Applications.Count == 0)
            {
                Console.WriteLine("No hay aplicaciones registradas.");
                Console.WriteLine();
                return;
            }

            var orderedApps = log.Applications
                .OrderByDescending(a => a.TotalMinutes)
                .ToList();

            foreach (var app in orderedApps)
            {
                Console.WriteLine(
                    $"{app.ProcessName,-20}  {app.TotalMinutes,4} min   " +
                    $"({app.FirstUse:HH:mm} - {app.LastUse:HH:mm})");
            }

            Console.WriteLine();
        }

       

        private static void PrintWebsites(DayLog log)
        {
            Console.WriteLine("== Sitios web (ordenados por tiempo) ==");

            if (log.Websites == null || log.Websites.Count == 0)
            {
                Console.WriteLine("No hay sitios web registrados.");
                Console.WriteLine();
                return;
            }

            var orderedSites = log.Websites
                .OrderByDescending(w => w.TotalMinutes)
                .ToList();

            foreach (var site in orderedSites)
            {
                Console.WriteLine(
                    $"{site.Domain,-30}  {site.TotalMinutes,4} min   " +
                    $"({site.FirstUse:HH:mm} - {site.LastUse:HH:mm})");
            }

            Console.WriteLine();
        }
        

    }

    // --------- Modelos (deben coincidir con el JSON del Monitor) ---------

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
