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

var appConfig = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configFile),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
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
        await HandleMessage(client, update.Message, text, ct);
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

    if (isJoining && wasOutside && userLevels.TryGetValue(userId, out var level))
        await AssignTag(client, monitoredGroupId.Value, userId, level, ct);
}

// ──────────────────────────────────────────────
// Tag helpers
// ──────────────────────────────────────────────
async Task TryAssignTagIfInGroup(ITelegramBotClient client, long userId, string category, CancellationToken ct)
{
    if (!monitoredGroupId.HasValue) return;
    try
    {
        var member = await client.GetChatMember(monitoredGroupId.Value, userId, ct);
        if (member.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator)
            await AssignTag(client, monitoredGroupId.Value, userId, category, ct);
    }
    catch { /* user not in group */ }
}

async Task AssignTag(ITelegramBotClient client, long groupId, long userId, string category, CancellationToken ct)
{
    try
    {
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
            steps[userId] = new Step(userId, chatId, StepStage.SelectingLanguage);
            // Greeting is shown before language is chosen, so send both languages
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
            var lang = steps.TryGetValue(userId, out var s) ? s.Language : "en";
            await client.SendMessage(chatId, Strings.Get(lang, "use_start"), cancellationToken: ct);
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

    if (!steps.TryGetValue(userId, out var step))
    {
        await client.SendMessage(chatId, Strings.Get("en", "session_expired"), cancellationToken: ct);
        return;
    }

    // Language selection
    if (step.Stage == StepStage.SelectingLanguage && data.StartsWith("lang:"))
    {
        step.Language = data["lang:".Length..];
        step.Stage = StepStage.AnsweringQuestions;
        step.CurrentQuestionIndex = 0;
        step.TotalPoints = 0;
        await SendQuestion(client, chatId, step, ct);
        return;
    }

    // Answer selection
    if (step.Stage == StepStage.AnsweringQuestions && data.StartsWith("ans:"))
    {
        var parts = data["ans:".Length..].Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var qIndex)
            && int.TryParse(parts[1], out var aIndex)
            && qIndex == step.CurrentQuestionIndex
            && qIndex < questions.Count
            && aIndex < questions[qIndex].Answers.Count)
        {
            // Edit the question message to show the selected answer
            var lang = step.Language;
            var q = questions[qIndex];
            var questionText = q.Text.GetValueOrDefault(lang) ?? q.Text.GetValueOrDefault("en") ?? "Question";
            var header = string.Format(Strings.Get(lang, "question_header"), qIndex + 1, questions.Count);
            var answerLines = q.Answers.Select((a, i) =>
            {
                var text = a.Text.GetValueOrDefault(lang) ?? a.Text.GetValueOrDefault("en") ?? $"Option {i + 1}";
                return i == aIndex ? $"✅ {text}" : $"▫️ {text}";
            });
            var editedText = $"{header}\n\n{questionText}\n\n{string.Join("\n", answerLines)}";
            await client.EditMessageText(chatId, callback.Message!.MessageId, editedText, cancellationToken: ct);

            step.TotalPoints += questions[qIndex].Answers[aIndex].Points;
            step.CurrentQuestionIndex++;

            if (step.CurrentQuestionIndex < questions.Count)
                await SendQuestion(client, chatId, step, ct);
            else
            {
                await SendResult(client, chatId, step, ct);
                steps.Remove(userId);
            }
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

async Task SendQuestion(ITelegramBotClient client, long chatId, Step step, CancellationToken ct)
{
    var lang = step.Language;
    var q = questions[step.CurrentQuestionIndex];
    var questionText = q.Text.GetValueOrDefault(lang) ?? q.Text.GetValueOrDefault("en") ?? "Question";

    var buttons = q.Answers.Select((a, i) =>
    {
        var answerText = a.Text.GetValueOrDefault(lang) ?? a.Text.GetValueOrDefault("en") ?? $"Option {i + 1}";
        return new[] { new InlineKeyboardButton(answerText) { CallbackData = $"ans:{step.CurrentQuestionIndex}:{i}" } };
    }).ToArray();

    var header = string.Format(Strings.Get(lang, "question_header"), step.CurrentQuestionIndex + 1, questions.Count);

    await client.SendMessage(chatId,
        $"{header}\n\n{questionText}",
        replyMarkup: new InlineKeyboardMarkup(buttons),
        cancellationToken: ct);
}

async Task SendResult(ITelegramBotClient client, long chatId, Step step, CancellationToken ct)
{
    var lang = step.Language;
    var total = step.TotalPoints;

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

    // Persist level and assign tag if already in monitored group
    userLevels[step.UserId] = category;
    SaveUserLevels();
    await TryAssignTagIfInGroup(client, step.UserId, category, ct);

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
            ["result"] =
                "Ready to play! \ud83c\udfbe Your Matchy Rank is {0}.\n" +
                "We've added you to the {1} group.\n\n" +
                "Before entering the group please change your Telegram name to:\n" +
                "Nickname | {2} | {0}\n\n" +
                "To do this go to Settings > My Account > Name.\n\n" +
                "Join the group via the link below.\n" +
                "Check the daily Poll for available slots.\n" +
                "Reply to the poll to find your partner.\n\n" +
                "{3}",
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
            ["result"] =
                "Готови за игра! \ud83c\udfbe Вашият Matchy ранг е {0}.\n" +
                "Добавихме ви в групата {1}.\n\n" +
                "Преди да влезете в групата, моля променете името си в Telegram на:\n" +
                "Псевдоним | {2} | {0}\n\n" +
                "За целта отидете в Настройки > Моят акаунт > Име.\n\n" +
                "Влезте в групата чрез линка по-долу.\n" +
                "Проверете ежедневната анкета за свободни места.\n" +
                "Отговорете на анкетата, за да намерите партньор.\n\n" +
                "{3}",
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
    SelectingLanguage,
    AnsweringQuestions,
    EditingQuestions
}

class Step(long userId, long chatId, StepStage stage)
{
    public long UserId { get; } = userId;
    public long ChatId { get; } = chatId;
    public StepStage Stage { get; set; } = stage;
    public string Language { get; set; } = "en";
    public int CurrentQuestionIndex { get; set; }
    public double TotalPoints { get; set; }
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
