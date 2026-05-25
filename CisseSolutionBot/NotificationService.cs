using System.Data;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

public class NotificationService
{
    private readonly Database _db;
    private readonly TelegramBotClient _bot;
    private Timer? _timer;
    private bool _isProcessing = false;

    public NotificationService(Database db, TelegramBotClient bot)
    {
        _db = db;
        _bot = bot;
    }

    public void Start()
    {
        _timer = new Timer(CheckAndSendNotifications, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        Console.WriteLine("✅ Service de notifications démarré (vérification toutes les 30 secondes)");
    }

    public void Stop()
    {
        _timer?.Dispose();
        Console.WriteLine("⏹️ Service de notifications arrêté");
    }

    private async void CheckAndSendNotifications(object? state)
    {
        if (_isProcessing) return;
        _isProcessing = true;

        try
        {
            var pendingNotifications = _db.GetPendingNotifications();

            foreach (DataRow row in pendingNotifications.Rows)
            {
                var notificationId = Convert.ToInt32(row["id_notification"]);
                var orderId = Convert.ToInt32(row["order_id"]);
                var telegramId = Convert.ToInt64(row["telegram_id"]);
                var type = row["type"].ToString();
                var total = row["total"] != DBNull.Value ? Convert.ToDecimal(row["total"]) : 0;

                string message = type switch
                {
                    "order_created" => GetOrderCreatedMessage(orderId, total),
                    "status_update" => GetStatusUpdateMessage(orderId, row["status"]?.ToString() ?? ""),
                    _ => GetDefaultMessage(orderId)
                };

                try
                {
                    await _bot.SendTextMessageAsync(telegramId, message, parseMode: ParseMode.Html);
                    _db.MarkNotificationSent(notificationId);
                    Console.WriteLine($"✅ Notification #{notificationId} envoyée à {telegramId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Erreur envoi notification #{notificationId}: {ex.Message}");
                }

                // Petit délai entre les messages
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erreur service notifications: {ex.Message}");
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private string GetOrderCreatedMessage(int orderId, decimal total)
    {
        var deliveryDate = DateTime.Now.AddDays(3).ToString("dd.MM.yyyy");

        return
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            "<b>🎉 ЗАКАЗ ПРИНЯТ!</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
            $"🆔 <b>Номер заказа:</b> #{orderId}\n" +
            $"💵 <b>Сумма:</b> {total:N0} ₽\n" +
            $"📅 <b>Ожидаемая доставка:</b> {deliveryDate}\n\n" +
            "📦 <i>Мы начнем обработку вашего заказа в ближайшее время.</i>\n\n" +
            "🔔 <b>Вы будете получать уведомления о статусе заказа.</b>";
    }

    private string GetStatusUpdateMessage(int orderId, string status)
    {
        var (icon, title, description) = status.ToLower() switch
        {
            "payée" or "payee" or "оплачен" => ("✅", "ЗАКАЗ ОПЛАЧЕН", "Ваш заказ оплачен и передан в обработку."),
            "preparation" or "готовится" => ("🔧", "ГОТОВИТСЯ К ОТПРАВКЕ", "Ваш заказ собирается на складе."),
            "expédiée" or "expediee" or "отправлен" => ("🚚", "ОТПРАВЛЕН", "Ваш заказ передан в службу доставки."),
            "livrée" or "livree" or "доставлен" => ("📦", "ДОСТАВЛЕН", "Ваш заказ доставлен. Благодарим за покупку!"),
            _ => ("ℹ️", "СТАТУС ОБНОВЛЕН", $"Статус вашего заказа: {status}")
        };

        return
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            $"<b>{icon} {title}</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
            $"🆔 <b>Заказ #{orderId}</b>\n" +
            $"📋 {description}\n\n" +
            $"📞 <i>Отследить заказ: /track {orderId}</i>";
    }

    private string GetDefaultMessage(int orderId)
    {
        return
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            "<b>📌 ОБНОВЛЕНИЕ СТАТУСА ЗАКАЗА</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
            $"🆔 <b>Заказ #{orderId}</b>\n" +
            $"Статус вашего заказа изменился.\n\n" +
            $"📞 <i>/track {orderId}</i> - для получения актуальной информации.";
    }
}