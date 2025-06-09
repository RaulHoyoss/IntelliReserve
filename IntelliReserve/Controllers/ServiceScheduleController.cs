using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IntelliReserve.Data;
using IntelliReserve.Models;
using IntelliReserve.Models.ViewModels;
using System.Diagnostics;
using System.Globalization;

namespace IntelliReserve.Controllers
{
    public class ServiceScheduleController : Controller
    {
        private readonly AppDbContext _context;

        public ServiceScheduleController(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var schedules = _context.ServiceSchedules.Include(s => s.Service);
            return View(await schedules.ToListAsync());
        }

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();
            var schedule = await _context.ServiceSchedules.Include(s => s.Service).FirstOrDefaultAsync(s => s.Id == id);
            if (schedule == null) return NotFound();
            return View(schedule);
        }

        public IActionResult Create() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("ServiceId,StartDateTime,EndDateTime")] ServiceSchedule schedule)
        {
            if (ModelState.IsValid)
            {
                _context.Add(schedule);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(schedule);
        }

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
            var schedule = await _context.ServiceSchedules.FindAsync(id);
            if (schedule == null) return NotFound();
            return View(schedule);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,ServiceId,StartDateTime,EndDateTime")] ServiceSchedule schedule)
        {
            if (id != schedule.Id) return NotFound();
            if (ModelState.IsValid)
            {
                _context.Update(schedule);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(schedule);
        }

        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();
            var schedule = await _context.ServiceSchedules.Include(s => s.Service).FirstOrDefaultAsync(s => s.Id == id);
            if (schedule == null) return NotFound();
            return View(schedule);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var schedule = await _context.ServiceSchedules.FindAsync(id);
            _context.ServiceSchedules.Remove(schedule);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }



        public async Task<IActionResult> ServiceCalendar(int serviceId)
        {
            var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == serviceId);
            if (service == null) return NotFound();

            var viewModel = new ServiceCalendarViewModel
            {
                ServiceId = service.Id,
                ServiceName = service.Name,
                ServiceDurationMinutes = service.Duration
            };

            return View("~/Views/Calendar/ServiceCalendar.cshtml", viewModel);
        }




        [HttpGet("/api/services/availability/{serviceId}")]
        public async Task<IActionResult> GetAvailability(int serviceId, [FromQuery] string start, [FromQuery] string end)
        {
            // Parsear las fechas 'start' y 'end' de la URL (rango de vista del calendario)
            // Ya vienen en formato ISO 8601 con 'Z' indicando UTC.
            if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime calendarStartDateUtc) ||
                !DateTime.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime calendarEndDateUtc))
            {
                return BadRequest("Invalid start or end date format. Expected YYYY-MM-ddTHH:mm:ssZ");
            }

            var service = await _context.Services
                .Include(s => s.Schedules)
                    .ThenInclude(ss => ss.Appointment) // Incluye los appointments asociados a los schedules
                .Include(s => s.AvailableDays)
                .FirstOrDefaultAsync(s => s.Id == serviceId);

            if (service == null)
                return NotFound();

            var result = new List<object>();

            var duration = TimeSpan.FromMinutes(service.Duration);

            //fecha acutal solo fecha
            var todayUtc = DateTime.UtcNow.Date;

            // Determinar el rango de fechas para el bucle en UTC, para comparar con ServiceSchedule.StartDateTime (que es UTC)
            var loopStartDateUtc = calendarStartDateUtc.Date; // Solo la fecha en UTC
            var loopEndDateUtc = calendarEndDateUtc.Date;     // Solo la fecha en UTC


            if (loopStartDateUtc < todayUtc)
            {
                loopStartDateUtc = todayUtc;
            }

            // Ajustar el loopEndDateUtc para incluir el �ltimo d�a completamente si la hora es 00:00:00Z
            if (loopEndDateUtc > loopStartDateUtc && calendarEndDateUtc.TimeOfDay == TimeSpan.Zero)
            {
                loopEndDateUtc = loopEndDateUtc.AddDays(-1);
            }

            if (loopStartDateUtc > loopEndDateUtc)
            {
                return Json(result); // Rango de fechas no v�lido, devuelve vac�o
            }

            // --- NUEVO: Obtener los ServiceSchedules existentes para el rango de fechas del calendario ---
            // Solo obtenemos los schedules que est�n dentro del rango de visualizaci�n solicitado.
            var existingSchedulesInCalendarRange = service.Schedules
                .Where(ss => ss.StartDateTime.Date >= loopStartDateUtc && ss.StartDateTime.Date <= loopEndDateUtc)
                .ToList();


            // 3. Iterar a trav�s de cada d�a en el rango `loopStartDateUtc` a `loopEndDateUtc`.
            for (var day = loopStartDateUtc; day <= loopEndDateUtc; day = day.AddDays(1))
            {
                // 4. Filtrar por AvailableDays (si est�n definidos)
                if (service.AvailableDays != null && service.AvailableDays.Any())
                {
                    if (!service.AvailableDays.Any(ad => ad.DayOfWeek == day.DayOfWeek))
                    {
                        continue; // Saltar si este d�a de la semana no est� disponible para el servicio
                    }
                }

                // --- NUEVO: Filtrar los horarios reales de la base de datos para este d�a ---
                // Esto es crucial para no generar slots "No disponible" para d�as sin schedules.
                var schedulesForThisDay = existingSchedulesInCalendarRange
                                            .Where(ss => ss.StartDateTime.Date == day.Date)
                                            .OrderBy(ss => ss.StartDateTime)
                                            .ToList();

                // Si no hay ning�n ServiceSchedule para este d�a, no generamos ning�n slot.
                if (!schedulesForThisDay.Any())
                {
                    continue; // Pasa al siguiente d�a en el bucle
                }

                // 5. Generar slots para las horas disponibles del d�a
                // Ahora, iteramos solo sobre los ServiceSchedules REALES para este d�a.
                foreach (var existingSchedule in schedulesForThisDay)
                {
                    // --- CORRECCI�N DESFASE HORARIO ---
                    // Los ServiceSchedule.StartDateTime y EndDateTime est�n en UTC
                    // Los convertimos a la hora local para la visualizaci�n en el cliente.
                    // Asumimos que tu zona horaria local tiene un desfase de +2 horas respecto a UTC.
                    // Puedes usar TimeZoneInfo.FindSystemTimeZoneById para una soluci�n m�s robusta.
                    // Para este ejemplo, a�adimos directamente las 2 horas.
                    var slotStartLocal = existingSchedule.StartDateTime.AddHours(2); // Ajuste para tu zona horaria local
                    var slotEndLocal = existingSchedule.EndDateTime.AddHours(2);     // Ajuste para tu zona horaria local

                    // FullCalendar espera fechas en formato ISO 8601 con 'Z' para UTC.
                    // Para mostrar las horas ajustadas en el calendario, necesitamos convertir estas horas locales ajustadas
                    // de nuevo a UTC para el formato FullCalendar, o simplemente pasarlas como locales
                    // y configurar FullCalendar para que las interprete como locales.
                    // La forma m�s robusta es enviar el DateTimeOffset o el TimeZone.
                    // Si FullCalendar espera Z (UTC), debes enviarlas como UTC.
                    // Si tu FullCalendar est� configurado para mostrar LOCAL, podemos enviar el string local.
                    // Asumiendo que FullCalendar procesa el 'Z' y quieres que la hora que se VE sea +2:
                    // La forma m�s sencilla es usar ToLocalTime() y formatear, si sabes que tu servidor est� en esa zona.
                    // O m�s robusto: crear un DateTimeOffset.
                    // Para simplificar, si el desfase de +2 es fijo, podemos simplemente ajustar la hora de salida.

                    // Si quieres que el calendario muestre las horas que t� ves en la base de datos (+2),
                    // Y FullCalendar espera UTC, entonces necesitas que el UTC que env�as
                    // sea 2 horas antes de la hora que quieres ver.
                    // Mejor opci�n: Env�a la hora UTC tal cual est� en la base de datos,
                    // y deja que FullCalendar maneje la conversi�n a la zona horaria del cliente.
                    // El problema de +2 horas podr�a ser una discrepancia entre c�mo creas las fechas
                    // al guardar y c�mo FullCalendar las interpreta.

                    // VAMOS A SIMPLIFICAR: Si tus ServiceSchedule.StartDateTime ya son UTC y la base de datos
                    // las muestra "retrasadas" 2 horas, significa que tu aplicaci�n al guardarlas no las ha
                    // convertido correctamente, o que FullCalendar las interpreta diferente.
                    // Si los datos de la base de datos son: "2025-06-08 10:00:00+02" (es UTC pero deber�a ser 08:00:00Z para mostrar 10:00:00 local)
                    // Y FullCalendar espera Z.
                    // Entonces, necesitamos ajustar el DateTime que sale.

                    // OPCI�N 1: La m�s correcta si tus ServiceSchedules.StartDateTime son realmente UTC.
                    // No necesitas a�adir 2 horas aqu�, sino asegurarte que al crear los schedules se guardaron bien UTC.
                    // Si ServiceSchedule.StartDateTime es 10:00:00Z, y quieres ver 12:00:00 local (+2),
                    // el cliente debe convertir.

                    // OPCI�N 2: Si el desfase de +2 es fijo y solo para la visualizaci�n en FullCalendar.
                    // Esto ajusta la hora que se env�a para que FullCalendar la muestre como deseamos.
                    var slotStartForDisplay = existingSchedule.StartDateTime.AddHours(2);
                    var slotEndForDisplay = existingSchedule.EndDateTime.AddHours(2);
                    // Aseguramos que la Kind sea Unspecified para que FullCalendar no intente convertirla de nuevo.
                    // O directamente la convertimos a un string sin Z y dejamos que FullCalendar la interprete como local.
                    string startString = slotStartForDisplay.ToString("yyyy-MM-ddTHH:mm:ss"); // Sin 'Z' para indicar que es hora local (FullCalendar default behavior)
                    string endString = slotEndForDisplay.ToString("yyyy-MM-ddTHH:mm:ss");     // Sin 'Z'

                    // --- FIN CORRECCI�N DESFASE HORARIO ---


                    string title = "Disponible";
                    string color = "#28a745"; // Verde
                    bool isBookable = true;
                    int? appointmentId = null;
                    int? serviceScheduleId = existingSchedule.Id; // El ID del ServiceSchedule ya lo tenemos aqu�

                    if (existingSchedule.Appointment != null) // Si el schedule tiene un appointment asociado
                    {
                        appointmentId = existingSchedule.Appointment.Id;
                        switch (existingSchedule.Appointment.Status)
                        {
                            case AppointmentStatus.Pending:
                                if (existingSchedule.Appointment.UserId != null)
                                {
                                    title = "Pendiente (Reservado)";
                                    color = "#ffc107"; // Amarillo/Naranja
                                    isBookable = false;
                                }
                                else
                                {
                                    // Status = Pending Y UserId = null, significa disponible para ser reservado por primera vez.
                                    title = "Disponible";
                                    color = "#28a745"; // Verde
                                    isBookable = true;
                                }
                                break;
                            case AppointmentStatus.Confirmed:
                                title = "Reservado";
                                color = "#dc3545"; // Rojo
                                isBookable = false;
                                break;
                            case AppointmentStatus.Completed:
                                title = "Completado";
                                color = "#007bff"; // Azul
                                isBookable = false;
                                break;
                            case AppointmentStatus.Canceled:
                                title = "Disponible (Cancelado)";
                                color = "#28a745"; // Verde, porque una cancelaci�n libera el slot
                                isBookable = true; // Se puede reservar de nuevo
                                break;
                            default:
                                title = "Estado Desconocido";
                                color = "#6c757d"; // Gris
                                isBookable = false;
                                break;
                        }
                    }
                    else
                    {
                        // Si el ServiceSchedule existe, pero NO tiene un Appointment asociado
                        // Esto ocurre si la generaci�n de schedules no crea appointments de inmediato.
                        // Seg�n tu base de datos, siempre tienes un appointment.
                        // Mantengo la l�gica por si acaso, pero no deber�a ser alcanzada con tu estructura 1:1.
                        title = "Disponible (sin cita)";
                        color = "#28a745";
                        isBookable = true;
                    }

                    result.Add(new
                    {
                        title = title,
                        start = startString, // Usar la cadena de tiempo ajustada
                        end = endString,     // Usar la cadena de tiempo ajustada
                        color = color,
                        id = serviceScheduleId,
                        appointmentId = appointmentId,
                        isBookable = isBookable
                    });
                }
            }
            return Json(result);
        }


        // Helper functions
        private DateTime Max(DateTime d1, DateTime d2) => d1 > d2 ? d1 : d2;
        private DateTime Min(DateTime d1, DateTime d2) => d1 < d2 ? d1 : d2;

    }
}
