using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

// ──────────────────────────────────────────────
// Load config.json
// ──────────────────────────────────────────────
var configFile = Path.Combine(AppContext.BaseDirectory, "config.json");
if (!File.Exists(configFile))
    configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "config.json");

if (!File.Exists(configFile))
    throw new FileNotFoundException("config.json not found. Copy config.json.example to config.json and fill in your values.");

Console.WriteLine(File.ReadAllText(configFile));

var appConfig = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configFile),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = false })
    ?? throw new InvalidOperationException("Failed to parse config.json.");

var botToken      = appConfig.BotToken      ?? throw new InvalidOperationException("BotToken is missing in config.json.");
var adminUserIds  = appConfig.AdminUserIds  ?? [];
var groupLinks    = appConfig.GroupLinks    ?? new Dictionary<string, string>();
var monitoredGroupId = appConfig.MonitoredGroupId;

// ──────────────────────────────────────────────
// User level persistence
// ──────────────────────────────────────────────
var userLevelsFilePath = Path.Combine(AppContext.BaseDirectory, "user_levels.json");
var userLevels = LoadUserLevels();

Dictionary<long, string> LoadUserLevels()
{
    if (File.Exists(userLevelsFilePath))
    {
        var json = File.ReadAllText(userLevelsFilePath);
        return JsonSerializer.Deserialize<Dictionary<long, string>>(json) ?? new();
    }
    return new();
}

void SaveUserLevels()
{
    var json = JsonSerializer.Serialize(userLevels, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(userLevelsFilePath, json);
}

var questionsFilePath = Path.Combine(AppContext.BaseDirectory, "questions.json");

if (!System.IO.File.Exists(questionsFilePath))
{
    var sourceFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "questions.json");
    if (System.IO.File.Exists(sourceFile))
        System.IO.File.Copy(sourceFile, questionsFilePath);
    else
        throw new FileNotFoundException("questions.json not found.");
}

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

List<Question> LoadQuestions()
{
    var json = System.IO.File.ReadAllText(questionsFilePath);
    var data = JsonSerializer.Deserialize<QuestionFile>(json, jsonOptions)
        ?? throw new InvalidOperationException("Failed to deserialize questions.json");
    return data.Questions;
}

void SaveQuestions(List<Question> questions)
{
    var data = new QuestionFile { Questions = questions };
    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    System.IO.File.WriteAllText(questionsFilePath, json);
}

var questions = LoadQuestions();

// ──────────────────────────────────────────────
// Step-based session tracking
// ──────────────────────────────────────────────
var steps = new Dictionary<long, Step>();

// ──────────────────────────────────────────────
// Bot setup
// ──────────────────────────────────────────────
using var cts = new CancellationTokenSource();
var bot = new TelegramBotClient(botToken);

bot.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandleErrorAsync,
    receiverOptions: new ReceiverOptions
    {
        AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.ChatMember, UpdateType.MyChatMember],
        DropPendingUpdates = true
    },
    cancellationToken: cts.Token
);

var me = await bot.GetMe(cts.Token);
Console.WriteLine($"Bot @{me.Username} is running. Press Ctrl+C to stop.");

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

// ──────────────────────────────────────────────
// Update handlers
// ──────────────────────────────────────────────
async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
{
    if (update.Type == UpdateType.Message && update.Message?.Text is { } text)
    {
        var from = update.Message.From;
        Console.WriteLine($"[MSG] {from?.Username ?? from?.FirstName ?? "unknown"} ({from?.Id}): {text}");
        await HandleMessage(client, update.Message, text, ct);
    }
    else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callback)
        await HandleCallback(client, callback, ct);
    else if (update.Type == UpdateType.ChatMember && update.ChatMember is { } chatMemberUpdate)
        await HandleChatMember(client, chatMemberUpdate, ct);
    else if (update.Type == UpdateType.MyChatMember && update.MyChatMember is { } myChatMember)
    {
        var newStatus = myChatMember.NewChatMember.Status;
        if (newStatus is ChatMemberStatus.Member or ChatMemberStatus.Administrator)
            Console.WriteLine($"[Group] Bot added to group: {myChatMember.Chat.Title} (ID: {myChatMember.Chat.Id})");
    }
}

async Task HandleErrorAsync(ITelegramBotClient client, Exception ex, HandleErrorSource source, CancellationToken ct)
{
    Console.WriteLine($"[Error] {source}: {ex.Message}");
    await Task.CompletedTask;
}

// ──────────────────────────────────────────────
// ChatMember handler — assign tag when user joins monitored group
// ──────────────────────────────────────────────
async Task HandleChatMember(ITelegramBotClient client, ChatMemberUpdated update, CancellationToken ct)
{
    if (monitoredGroupId == null || update.Chat.Id != monitoredGroupId) return;

    var newStatus = update.NewChatMember.Status;
    var oldStatus = update.OldChatMember.Status;
    var userId = update.NewChatMember.User.Id;

    bool isJoining = newStatus is ChatMemberStatus.Member or ChatMemberStatus.Administrator;
    bool wasOutside = oldStatus is ChatMemberStatus.Left or ChatMemberStatus.Kicked or ChatMemberStatus.Restricted;

    if (isJoining && wasOutside)
    {
        if (userLevels.TryGetValue(userId, out var level))
            await AssignTag(client, monitoredGroupId.Value, userId, level, ct);

        // Send welcome DM
        try
        {
            var rank = userLevels.TryGetValue(userId, out var lvl)
                ? lvl
                : "Unknown";
            var welcomeMsg =
                $"{Strings.Get("en", "welcome_group")}\n\n" +
                $"{Strings.Get("bg", "welcome_group")}";
            await client.SendMessage(userId, welcomeMsg, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Welcome DM] Could not send to {userId}: {ex.Message}");
        }
    }
}

// ──────────────────────────────────────────────
// Tag helpers
// ──────────────────────────────────────────────
async Task TryAssignTagIfInGroup(ITelegramBotClient client, long userId, string category, CancellationToken ct)
{
    Console.WriteLine("burada2");

    if (!monitoredGroupId.HasValue) return;
    try
    {
        Console.WriteLine("burada2.5");

        var member = await client.GetChatMember(monitoredGroupId.Value, userId, ct);
        if (member.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator)
            await AssignTag(client, monitoredGroupId.Value, userId, category, ct);

        Console.WriteLine("burada2.9");
    }
    catch (Exception ex){
        Console.WriteLine(ex);
    }
}

async Task AssignTag(ITelegramBotClient client, long groupId, long userId, string category, CancellationToken ct)
{
    try
    {
        Console.WriteLine("burada3");
        await client.SetChatMemberTag(groupId, userId, category, ct);
        Console.WriteLine($"[Tag] Assigned '{category}' to user {userId} in group {groupId}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Tag] Failed for user {userId}: {ex.Message}");
    }
}

// ──────────────────────────────────────────────
// Message handler
// ──────────────────────────────────────────────
async Task HandleMessage(ITelegramBotClient client, Message message, string text, CancellationToken ct)
{
    var chatId = message.Chat.Id;
    var userId = message.From!.Id;

    if (message.Chat.Type != ChatType.Private)
    {
        Console.WriteLine($"[GROUP] {message.Chat.Title} ({message.Chat.Id}) | {message.From?.Username ?? message.From?.FirstName ?? "unknown"}: {text}");
        return;
    }

    // Admin edit mode: expect raw JSON
    if (steps.TryGetValue(userId, out var currentStep) && currentStep.Stage == StepStage.EditingQuestions)
    {
        if (text.TrimStart().StartsWith("/cancel"))
        {
            steps.Remove(userId);
            await client.SendMessage(chatId, "Edit cancelled.", cancellationToken: ct);
            return;
        }

        try
        {
            var newData = JsonSerializer.Deserialize<QuestionFile>(text, jsonOptions);
            if (newData?.Questions is null || newData.Questions.Count == 0)
            {
                steps.Remove(userId);
                await client.SendMessage(chatId, "Invalid JSON. Edit cancelled. Send /editquestions to try again.", cancellationToken: ct);
                return;
            }
            questions = newData.Questions;
            SaveQuestions(questions);
            steps.Remove(userId);
            await client.SendMessage(chatId, $"Questions updated successfully! ({questions.Count} questions loaded.)", cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            steps.Remove(userId);
            await client.SendMessage(chatId, $"JSON parse error: {ex.Message}\nEdit cancelled. Send /editquestions to try again.", cancellationToken: ct);
        }
        return;
    }

    switch (text.Split(' ')[0].ToLowerInvariant())
    {
        case "/start":
            await client.SendMessage(chatId,
                "Hello! To help us direct you to the right group, please answer the following questions.\n\n" +
                "Здравейте! За да ви насочим към правилната група, моля отговорете на следните въпроси.",
                cancellationToken: ct);
            await SendLanguageSelection(client, chatId, ct);
            break;

        case "/editquestions":
            if (!adminUserIds.Contains(userId))
            {
                await client.SendMessage(chatId, "You are not authorized to use this command.", cancellationToken: ct);
                return;
            }
            steps[userId] = new Step(userId, chatId, StepStage.EditingQuestions);
            var currentJson = JsonSerializer.Serialize(new QuestionFile { Questions = questions },
                new JsonSerializerOptions { WriteIndented = true });
            await client.SendMessage(chatId,
                "Current questions JSON:\n\n" +
                $"```json\n{currentJson}\n```\n\n" +
                "Send the full updated JSON to replace the questions, or /cancel to abort.",
                parseMode: ParseMode.Markdown,
                cancellationToken: ct);
            break;

        case "/cancel":
            if (steps.TryGetValue(userId, out var step) && step.Stage == StepStage.EditingQuestions)
            {
                steps.Remove(userId);
                await client.SendMessage(chatId, "Edit cancelled.", cancellationToken: ct);
            }
            break;

        default:
            await client.SendMessage(chatId, Strings.Get("en", "use_start"), cancellationToken: ct);
            break;
    }
}

// ──────────────────────────────────────────────
// Callback handler
// ──────────────────────────────────────────────
async Task HandleCallback(ITelegramBotClient client, CallbackQuery callback, CancellationToken ct)
{
    var chatId = callback.Message!.Chat.Id;
    var userId = callback.From.Id;
    var data = callback.Data ?? "";

    await client.AnswerCallbackQuery(callback.Id, cancellationToken: ct);

    // Language selection — stateless, just start question 0
    if (data.StartsWith("lang:"))
    {
        var lang = data["lang:".Length..];
        await SendQuestion(client, chatId, 0, lang, 0.0, ct);
        return;
    }

    // Answer selection — format: ans:{qIndex}:{aIndex}:{lang}:{totalPoints}
    if (data.StartsWith("ans:"))
    {
        var parts = data["ans:".Length..].Split(':');
        if (parts.Length == 4
            && int.TryParse(parts[0], out var qIndex)
            && int.TryParse(parts[1], out var aIndex)
            && double.TryParse(parts[3], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var prevPoints)
            && qIndex < questions.Count
            && aIndex < questions[qIndex].Answers.Count)
        {
            var lang = parts[2];

            // Edit the question message to show the selected answer
            var q = questions[qIndex];
            var questionText = q.Text.GetValueOrDefault(lang) ?? q.Text.GetValueOrDefault("en") ?? "Question";
            var header = string.Format(Strings.Get(lang, "question_header"), qIndex + 1, questions.Count);
            var answerLines = q.Answers.Select((a, i) =>
            {
                var text = a.Text.GetValueOrDefault(lang) ?? a.Text.GetValueOrDefault("en") ?? $"Option {i + 1}";
                return i == aIndex ? $"✅ {text}" : $"▫️ {text}";
            });
            await client.EditMessageText(chatId, callback.Message!.MessageId,
                $"{header}\n\n{questionText}\n\n{string.Join("\n", answerLines)}", cancellationToken: ct);

            var newPoints = prevPoints + questions[qIndex].Answers[aIndex].Points;
            var nextIndex = qIndex + 1;

            if (nextIndex < questions.Count)
                await SendQuestion(client, chatId, nextIndex, lang, newPoints, ct);
            else
                await SendResult(client, chatId, userId, lang, newPoints, ct);
        }
    }
}

// ──────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────
async Task SendLanguageSelection(ITelegramBotClient client, long chatId, CancellationToken ct)
{
    var keyboard = new InlineKeyboardMarkup(new[]
    {
        new[] { new InlineKeyboardButton("🇬🇧 English")  { CallbackData = "lang:en" } },
        new[] { new InlineKeyboardButton("🇧🇬 Български") { CallbackData = "lang:bg" } },
    });

    // Language prompt shown before a language is chosen — display both
    await client.SendMessage(chatId,
        "Please choose your language / Моля изберете език:",
        replyMarkup: keyboard,
        cancellationToken: ct);
}

async Task SendQuestion(ITelegramBotClient client, long chatId, int qIndex, string lang, double totalPoints, CancellationToken ct)
{
    var q = questions[qIndex];
    var questionText = q.Text.GetValueOrDefault(lang) ?? q.Text.GetValueOrDefault("en") ?? "Question";
    var pts = totalPoints.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

    var buttons = q.Answers.Select((a, i) =>
    {
        var answerText = a.Text.GetValueOrDefault(lang) ?? a.Text.GetValueOrDefault("en") ?? $"Option {i + 1}";
        return new[] { new InlineKeyboardButton(answerText) { CallbackData = $"ans:{qIndex}:{i}:{lang}:{pts}" } };
    }).ToArray();

    var header = string.Format(Strings.Get(lang, "question_header"), qIndex + 1, questions.Count);

    await client.SendMessage(chatId,
        $"{header}\n\n{questionText}",
        replyMarkup: new InlineKeyboardMarkup(buttons),
        cancellationToken: ct);
}

async Task SendResult(ITelegramBotClient client, long chatId, long userId, string lang, double total, CancellationToken ct)
{
    var (rankKey, category) = total switch
    {
        < 0.5  => ("rank_beginner",     "Beginner"),
        < 1.0  => ("rank_intermediate", "Intermediate"),
        < 1.4  => ("rank_advanced",     "Advanced"),
        _      => ("rank_expert",       "Expert"),
    };

    var rank = Strings.Get(lang, rankKey);
    var score = total.ToString("F2");
    var link = groupLinks.GetValueOrDefault(category) ?? "https://t.me/+DEFAULT_LINK";

    userLevels[userId] = category;
    SaveUserLevels();
    await TryAssignTagIfInGroup(client, userId, category, ct);

    var resultMessage = string.Format(Strings.Get(lang, "result"), rank, category, score, link);

    await client.SendMessage(chatId, resultMessage, cancellationToken: ct);
}

// ──────────────────────────────────────────────
// Translations
// ──────────────────────────────────────────────
static class Strings
{
    private static readonly Dictionary<string, Dictionary<string, string>> _translations = new()
    {
        ["en"] = new()
        {
            ["use_start"]       = "Send /start to begin the questionnaire.",
            ["session_expired"] = "Session expired. Send /start to begin again.",
            ["question_header"] = "Question {0}/{1}:",
            ["rank_beginner"]     = "Beginner",
            ["rank_intermediate"] = "Intermediate",
            ["rank_advanced"]     = "Advanced",
            ["rank_expert"]       = "Expert / Pro",
            ["welcome_group"] =
                "🎾 Welcome to the Matchy Tennis group!\n\n" +
                "A few reminders:\n" +
                "• Check the daily Poll to find available slots.\n" +
                "• Reply to the poll to find your partner.\n" +
                "• Keep your Telegram name in the format: Nickname | Level | Rank\n\n" +
                "Good luck and enjoy your game! 🏆",
            ["result"] =
                "Ready to play! \ud83c\udfbe Your Matchy Rank is {0}.\n" +
                "We've added you to the {0} group.\n\n" +
                "Before entering the group please change your Telegram name to:\n" +
                "Nickname | {1} | {0}\n\n" +
                "To do this go to Settings > My Account > Name.\n\n" +
                "Join the main group first:\n" +
                "https://t.me/+Tb0fiNX6XT80M2Jk\n\n" +
                "Then join your level channel:\n" +
                "{3}\n\n" +
                "Check the daily Poll for available slots.\n" +
                "Reply to the poll to find your partner.",
        },
        ["bg"] = new()
        {
            ["use_start"]       = "Изпратете /start, за да започнете въпросника.",
            ["session_expired"] = "Сесията изтече. Изпратете /start, за да започнете отново.",
            ["question_header"] = "Въпрос {0}/{1}:",
            ["rank_beginner"]     = "Начинаещ",
            ["rank_intermediate"] = "Средно напреднал",
            ["rank_advanced"]     = "Напреднал",
            ["rank_expert"]       = "Експерт / Про",
            ["welcome_group"] =
                "🎾 Добре дошли в групата на Matchy Tennis!\n\n" +
                "Няколко напомняния:\n" +
                "• Проверявайте ежедневната анкета за свободни места.\n" +
                "• Отговорете на анкетата, за да намерите партньор.\n" +
                "• Запазете Telegram името си във формат: Псевдоним | Ниво | Ранг\n\n" +
                "Успех и приятна игра! 🏆",
            ["result"] =
                "Готови за игра! \ud83c\udfbe Вашият Matchy ранг е {0}.\n" +
                "Добавихме ви в групата {0}.\n\n" +
                "Преди да влезете в групата, моля променете името си в Telegram на:\n" +
                "Псевдоним | {1} | {0}\n\n" +
                "За целта отидете в Настройки > Моят акаунт > Име.\n\n" +
                "Първо влезте в основната група:\n" +
                "https://t.me/+Tb0fiNX6XT80M2Jk\n\n" +
                "След това влезте в канала за вашето ниво:\n" +
                "{3}\n\n" +
                "Проверете ежедневната анкета за свободни места.\n" +
                "Отговорете на анкетата, за да намерите партньор.",
        },
    };

    public static string Get(string lang, string key)
    {
        if (_translations.TryGetValue(lang, out var table) && table.TryGetValue(key, out var value))
            return value;
        // Fall back to English
        return _translations["en"].GetValueOrDefault(key, key);
    }
}

// ──────────────────────────────────────────────
// Models
// ──────────────────────────────────────────────
enum StepStage
{
    EditingQuestions
}

class Step(long userId, long chatId, StepStage stage)
{
    public long UserId { get; } = userId;
    public long ChatId { get; } = chatId;
    public StepStage Stage { get; set; } = stage;
}

class QuestionFile
{
    public List<Question> Questions { get; set; } = [];
}

class Question
{
    public Dictionary<string, string> Text { get; set; } = [];
    public List<Answer> Answers { get; set; } = [];
}

class Answer
{
    public Dictionary<string, string> Text { get; set; } = [];
    public double Points { get; set; }
}

class AppConfig
{
    public string? BotToken { get; set; }
    public long[] AdminUserIds { get; set; } = [];
    public long? MonitoredGroupId { get; set; }
    public Dictionary<string, string> GroupLinks { get; set; } = new();
}
