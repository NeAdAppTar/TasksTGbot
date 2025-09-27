using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using System.Collections.Concurrent;
using System.Text.Json;
using File = System.IO.File;

namespace TodoBot;

class Program
{
    record TaskItem(string Text, bool Done);

    static readonly long SuperAdminId = 1301873508;
    static ConcurrentDictionary<long, List<TaskItem>> tasks = new();
    static HashSet<long> allowedIds = new();
    static string BotUsername = "";

    static async Task Main()
    {
        tasks = LoadTasks();
        allowedIds = LoadAllowed();

        string token = "TOKEN";
        var bot = new TelegramBotClient(token);

        using var cts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        bot.StartReceiving(HandleUpdate, HandleError, receiverOptions, cts.Token);

        var me = await bot.GetMeAsync();
        BotUsername = me.Username ?? "";
        Console.WriteLine($"Бот @{me.Username} запущен...");

        Console.ReadLine();
        cts.Cancel();
    }

    static async Task HandleUpdate(ITelegramBotClient bot, Update update, CancellationToken token)
    {
        if (update.Message is not { } message || message.Text is not { } msgText)
            return;

        long chatId = message.Chat.Id;
        long userId = message.From?.Id ?? 0;

        // проверка доступа
        if (userId != SuperAdminId && chatId != SuperAdminId)
        {
            if (!allowedIds.Contains(chatId) && !allowedIds.Contains(userId))
            {
                await bot.SendTextMessageAsync(chatId, "⛔ У вас нет доступа к боту", cancellationToken: token);
                return;
            }
        }

        // нормализуем команду
        string cmd = NormalizeCommand(msgText, BotUsername);

        // --- команды ---
        if (cmd.StartsWith("/add "))
        {
            string taskText = cmd[5..].Trim();
            tasks.AddOrUpdate(chatId,
                new List<TaskItem> { new TaskItem(taskText, false) },
                (k, v) => { v.Add(new TaskItem(taskText, false)); return v; });

            SaveTasks(tasks);
            await bot.SendTextMessageAsync(chatId, $"Добавлено: {taskText}", cancellationToken: token);
        }
        else if (cmd == "/list")
        {
            if (!tasks.ContainsKey(chatId) || tasks[chatId].Count == 0)
            {
                await bot.SendTextMessageAsync(chatId, "Список пуст.", cancellationToken: token);
            }
            else
            {
                string list = string.Join("\n", tasks[chatId]
                    .Select((t, i) => $"{i + 1}. {(t.Done ? "✅" : "❌")} {t.Text}"));

                await bot.SendTextMessageAsync(chatId, $"📋 Список задач:\n{list}", cancellationToken: token);
            }
        }
        else if (cmd.StartsWith("/done "))
        {
            if (int.TryParse(cmd[6..], out int num))
            {
                if (tasks.ContainsKey(chatId) && num > 0 && num <= tasks[chatId].Count)
                {
                    var item = tasks[chatId][num - 1];
                    tasks[chatId][num - 1] = item with { Done = true };

                    SaveTasks(tasks);
                    await bot.SendTextMessageAsync(chatId, $"✅ Отмечено выполненным: {item.Text}", cancellationToken: token);
                }
                else
                {
                    await bot.SendTextMessageAsync(chatId, "Неверный номер задачи.", cancellationToken: token);
                }
            }
        }
        else if (cmd.StartsWith("/remove "))
        {
            if (int.TryParse(cmd[8..], out int num))
            {
                if (tasks.ContainsKey(chatId) && num > 0 && num <= tasks[chatId].Count)
                {
                    var removed = tasks[chatId][num - 1];
                    tasks[chatId].RemoveAt(num - 1);

                    SaveTasks(tasks);
                    await bot.SendTextMessageAsync(chatId, $"🗑 Удалено: {removed.Text}", cancellationToken: token);
                }
                else
                {
                    await bot.SendTextMessageAsync(chatId, "Неверный номер задачи.", cancellationToken: token);
                }
            }
        }
        else if (cmd == "/clear")
        {
            tasks[chatId] = new List<TaskItem>();
            SaveTasks(tasks);
            await bot.SendTextMessageAsync(chatId, "🧹 Все задачи удалены", cancellationToken: token);
        }
        else if (cmd.StartsWith("/allow "))
        {
            if (long.TryParse(cmd[7..], out long id))
            {
                allowedIds.Add(id);
                SaveAllowed(allowedIds);
                await bot.SendTextMessageAsync(chatId, $"✅ Доступ разрешён для {id}", cancellationToken: token);
            }
        }
        else if (cmd.StartsWith("/deny "))
        {
            if (long.TryParse(cmd[6..], out long id))
            {
                if (allowedIds.Remove(id))
                {
                    SaveAllowed(allowedIds);
                    await bot.SendTextMessageAsync(chatId, $"❌ Доступ запрещён для {id}", cancellationToken: token);
                }
                else
                {
                    await bot.SendTextMessageAsync(chatId, $"ℹ️ ID {id} не найден в списке", cancellationToken: token);
                }
            }
        }
        else if (cmd == "/help")
        {
            await bot.SendTextMessageAsync(chatId,
                "Команды:\n" +
                "/add <задача> – добавить задачу\n" +
                "/list – показать список\n" +
                "/done <номер> – отметить выполненной\n" +
                "/remove <номер> – удалить задачу\n" +
                "/clear – удалить все задачи\n\n" +
                "Управление доступом (только админ):\n" +
                "/allow <id>\n" +
                "/deny <id>",
                cancellationToken: token);
        }
    }

    static string NormalizeCommand(string text, string? botUsername)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (string.IsNullOrEmpty(botUsername)) return text;
        return text.Replace("@" + botUsername, "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    static Task HandleError(ITelegramBotClient bot, Exception ex, CancellationToken token)
    {
        Console.WriteLine($"Ошибка: {ex.Message}");
        return Task.CompletedTask;
    }

    // сохранение задач
    static void SaveTasks(ConcurrentDictionary<long, List<TaskItem>> data)
    {
        File.WriteAllText("tasks.json", JsonSerializer.Serialize(data));
    }
    static ConcurrentDictionary<long, List<TaskItem>> LoadTasks()
    {
        if (File.Exists("tasks.json"))
        {
            var json = File.ReadAllText("tasks.json");
            return JsonSerializer.Deserialize<ConcurrentDictionary<long, List<TaskItem>>>(json)
                   ?? new ConcurrentDictionary<long, List<TaskItem>>();
        }
        return new ConcurrentDictionary<long, List<TaskItem>>();
    }

    // сохранение разрешённых
    static void SaveAllowed(HashSet<long> ids)
    {
        File.WriteAllText("allowed.json", JsonSerializer.Serialize(ids));
    }
    static HashSet<long> LoadAllowed()
    {
        if (File.Exists("allowed.json"))
        {
            var json = File.ReadAllText("allowed.json");
            return JsonSerializer.Deserialize<HashSet<long>>(json) ?? new HashSet<long>();
        }
        return new HashSet<long>();
    }
}
