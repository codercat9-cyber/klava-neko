using System.Text;
using System.Text.Json;

namespace KlavaDesk;

/// <summary>
/// Долгая задача Клавы — то, что не влезает в один ход разговора: «спроси у Клода его журнал и по нему настрой себе
/// голосовой сервер». Раньше каждый ход жил сам по себе: пара команд, 150 секунд бюджета — ход кончился, и задача
/// забывалась. Теперь у задачи есть цель, план из шагов с заметками (что нашла: пути, версии, ответы), и она лежит в
/// data\task.json. Пока задача не закрыта (task_done / task_cancel), Клава сама к ней возвращается: сразу после хода
/// или по будильнику task_wait (ждёт ответа другой нейронки, сборки, загрузки). Предохранители: не больше
/// <see cref="MaxContinuations"/> возвратов и <see cref="MaxDuration"/> на задачу, «стоп» друга снимает её.
/// </summary>
internal sealed class TaskBoard
{
    public const int MaxContinuations = 40;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(45);

    public sealed class Step
    {
        public string Text { get; set; } = "";
        /// <summary>todo | doing | done | failed | skip</summary>
        public string Status { get; set; } = "todo";
        public string Note { get; set; } = "";
    }

    public sealed class State
    {
        public string Goal { get; set; } = "";
        public string For { get; set; } = "";
        public List<Step> Steps { get; set; } = [];
        public List<string> Notes { get; set; } = [];
        public DateTime Started { get; set; }
        public DateTime? WakeAt { get; set; }
        public string WaitReason { get; set; } = "";
        public int Continuations { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _file;
    private readonly object _gate = new();
    private State? _s;

    /// <summary>Задача закрыта: (итог, отменена ли) — для пузыря и журнала.</summary>
    public event Action<string, bool>? Finished;

    public TaskBoard(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file)) _s = JsonSerializer.Deserialize<State>(File.ReadAllText(file), Json);
            if (_s is { Goal.Length: 0 }) _s = null;
            // задача, брошенная давно (Клаву выключали на ночь), — не воскрешать
            if (_s is not null && DateTime.Now - _s.Started > MaxDuration * 2) { _s = null; File.Delete(file); }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _s = null;
        }
    }

    public bool Active { get { lock (_gate) return _s is not null; } }

    public string Goal { get { lock (_gate) return _s?.Goal ?? ""; } }

    private void Save()
    {
        try
        {
            if (_s is null) { if (File.Exists(_file)) File.Delete(_file); return; }
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_s, Json), new UTF8Encoding(false));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (IOException ex) { Log.Error("задача: запись", ex); }
    }

    public string Start(string goal, IEnumerable<string> steps, string who)
    {
        goal = goal.Trim();
        if (goal.Length == 0) return "❌ Не сказано, какая цель у задачи.";
        var list = steps.Select(x => x.Trim()).Where(x => x.Length > 0).Take(20).Select(x => new Step { Text = x }).ToList();
        string? replaced;
        lock (_gate)
        {
            replaced = _s?.Goal;
            _s = new State { Goal = goal, For = who, Steps = list, Started = DateTime.Now };
            if (list.Count > 0) list[0].Status = "doing";
            Save();
        }
        Log.Write($"задача: начата «{goal}» ({list.Count} шагов){(replaced is null ? "" : $", прежняя «{replaced}» заменена")}");
        return $"Задача заведена: «{goal}». Шагов в плане: {list.Count}. Делай по шагам, отмечай task_step, ждать — task_wait, всё готово и проверено — task_done." +
               (replaced is null ? "" : $" (Прежняя задача «{replaced}» закрыта этой.)");
    }

    /// <summary>Отметить шаг: done/failed/skip/doing, с заметкой (что нашла — пути, версии, ответы). index — с 1.</summary>
    public string UpdateStep(int index, string status, string note, string? text)
    {
        lock (_gate)
        {
            if (_s is null) return "❌ Задачи нет — сначала task_start.";
            status = status.Trim().ToLowerInvariant() switch
            {
                "done" or "ok" or "готово" or "сделано" => "done",
                "failed" or "fail" or "error" or "не вышло" => "failed",
                "skip" or "skipped" or "пропуск" => "skip",
                "doing" or "в работе" => "doing",
                _ => "done",
            };
            Step step;
            if (index >= 1 && index <= _s.Steps.Count) step = _s.Steps[index - 1];
            else
            {
                // новый шаг (план уточнился по ходу дела)
                step = new Step { Text = string.IsNullOrWhiteSpace(text) ? note : text! };
                _s.Steps.Add(step);
                index = _s.Steps.Count;
            }
            if (!string.IsNullOrWhiteSpace(text)) step.Text = text!.Trim();
            step.Status = status;
            if (!string.IsNullOrWhiteSpace(note)) step.Note = note.Trim().Length > 600 ? note.Trim()[..600] + "…" : note.Trim();
            if (status is "done" or "skip" && _s.Steps.FirstOrDefault(x => x.Status == "todo") is { } next) next.Status = "doing";
            Save();
            var left = _s.Steps.Count(x => x.Status is "todo" or "doing");
            return $"Шаг {index} — {Mark(status)}. Осталось шагов: {left}." + (left == 0 ? " Всё по плану сделано: проверь результат и закрой task_done." : "");
        }
    }

    /// <summary>Подождать (ответ другой нейронки, сборка, загрузка): вернёшься к задаче через seconds.</summary>
    public string Wait(int seconds, string reason)
    {
        seconds = Math.Clamp(seconds <= 0 ? 60 : seconds, 10, 1800);
        lock (_gate)
        {
            if (_s is null) return "❌ Задачи нет — ждать нечего.";
            _s.WakeAt = DateTime.Now.AddSeconds(seconds);
            _s.WaitReason = reason.Trim();
            Save();
        }
        Log.Write($"задача: жду {seconds} с — {reason}");
        return $"Будильник на {seconds} с: система сама вернёт тебя к задаче. Сейчас закончи ход коротко (что ждёшь).";
    }

    public string Note(string text)
    {
        lock (_gate)
        {
            if (_s is null) return "❌ Задачи нет.";
            _s.Notes.Add($"{DateTime.Now:HH:mm} {text.Trim()}");
            if (_s.Notes.Count > 30) _s.Notes.RemoveAt(0);
            Save();
            return "Записала в заметки задачи.";
        }
    }

    public string Finish(string summary, bool cancelled)
    {
        string goal;
        lock (_gate)
        {
            if (_s is null) return "Задачи и так нет.";
            goal = _s.Goal;
            _s = null;
            Save();
        }
        summary = summary.Trim();
        Log.Write($"задача: {(cancelled ? "отменена" : "готова")} «{goal}» — {summary}");
        Finished?.Invoke(string.IsNullOrEmpty(summary) ? goal : summary, cancelled);
        return cancelled ? $"Задача «{goal}» снята. Скажи другу честно, почему и что успела." : $"Задача «{goal}» закрыта. Коротко расскажи другу итог.";
    }

    /// <summary>Пора продолжать: задача есть, будильник прозвенел (или его нет).</summary>
    public bool Due(DateTime now)
    {
        lock (_gate) return _s is not null && (_s.WakeAt is null || now >= _s.WakeAt);
    }

    /// <summary>Предохранитель: слишком много возвратов или слишком долго — пора честно отчитаться и остановиться.</summary>
    public string? Exhausted()
    {
        lock (_gate)
        {
            if (_s is null) return null;
            if (_s.Continuations >= MaxContinuations) return $"уже {_s.Continuations} подходов";
            if (DateTime.Now - _s.Started > MaxDuration) return $"идёт {(int)(DateTime.Now - _s.Started).TotalMinutes} мин";
            return null;
        }
    }

    public void MarkContinued()
    {
        lock (_gate)
        {
            if (_s is null) return;
            _s.Continuations++;
            _s.WakeAt = null;
            _s.WaitReason = "";
            Save();
        }
    }

    private static string Mark(string status) => status switch
    {
        "done" => "✅ сделано", "failed" => "❌ не вышло", "skip" => "⏭ пропущен", "doing" => "⏳ в работе", _ => "▫ впереди",
    };

    /// <summary>Задача текстом: цель, план с отметками и заметками, сколько идёт, чего ждали.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_s is null) return "";
            var sb = new StringBuilder();
            sb.AppendLine($"Цель: {_s.Goal}{(_s.For.Length > 0 ? $" (просил {_s.For})" : "")}. Идёт {(int)(DateTime.Now - _s.Started).TotalMinutes} мин, подход {_s.Continuations + 1}.");
            if (_s.Steps.Count > 0)
            {
                sb.AppendLine("План:");
                for (var i = 0; i < _s.Steps.Count; i++)
                {
                    var st = _s.Steps[i];
                    sb.AppendLine($"  {i + 1}. {Mark(st.Status)} {st.Text}{(st.Note.Length > 0 ? $" — {st.Note}" : "")}");
                }
            }
            if (_s.Notes.Count > 0) sb.AppendLine("Заметки: " + string.Join(" | ", _s.Notes.TakeLast(8)));
            if (_s.WaitReason.Length > 0) sb.AppendLine($"Ты ждала: {_s.WaitReason}");
            return sb.ToString().TrimEnd();
        }
    }
}
