using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Data;

class Program
{
    static TelegramBotClient? bot;
    static Database? db;
    static Dictionary<long, string> userStates = new Dictionary<long, string>();
    static Dictionary<long, int> pendingOrders = new Dictionary<long, int>();
    static Dictionary<long, string> userLanguages = new Dictionary<long, string>();
    static IConfigurationRoot? config;
    static NotificationService? notificationService;

    static async Task Main(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════╗");
        Console.WriteLine("║     🤖 TechShop Pro - Bot Commerce     ║");
        Console.WriteLine("╚════════════════════════════════════════╝");

        config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .Build();

        var token = config["TelegramBotToken"];
        var connectionString = config.GetConnectionString("DefaultConnection");

        if (string.IsNullOrEmpty(token) || token == "VOTRE_TOKEN_ICI")
        {
            Console.WriteLine("❌ ERREUR: Token manquant!");
            return;
        }

        db = new Database(connectionString);
        bot = new TelegramBotClient(token);

        db.TestConnection();

        notificationService = new NotificationService(db, bot);
        notificationService.Start();

        bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, null, default);

        Console.WriteLine("✅ TechShop Pro démarré !");

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n🛑 Arrêt du bot...");
            notificationService?.Stop();
            Environment.Exit(0);
        };

        await Task.Delay(-1);
    }

    static async Task SendHtmlMessage(long chatId, string message, IReplyMarkup? replyMarkup = null)
    {
        await bot!.SendTextMessageAsync(chatId, message, parseMode: ParseMode.Html, replyMarkup: replyMarkup);
    }

    static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is not null)
        {
            await HandleCallbackAsync(bot, update.CallbackQuery, ct);
            return;
        }

        if (update.Message is not Message message) return;

        var chatId = message.Chat.Id;
        var lang = await GetUserLanguage(chatId);

        if (userStates.ContainsKey(chatId) && userStates[chatId] == "waiting_phone")
        {
            if (message.Contact is not null)
            {
                var phone = message.Contact.PhoneNumber;
                db!.SaveUserPhone(chatId, phone);
                userStates[chatId] = "waiting_address";
                await SendHtmlMessage(chatId, "📞 <b>Номер сохранён!</b>\n\n📍 Введите адрес доставки:");
                return;
            }
            else
            {
                await SendHtmlMessage(chatId, "❌ <b>Ошибка</b>\nИспользуйте кнопку «Поделиться номером»");
                return;
            }
        }

        if (userStates.ContainsKey(chatId) && userStates[chatId] == "waiting_address")
        {
            await ShowOrderConfirmation(chatId, message.Text!, lang);
            return;
        }

        if (message.Text is not string text) return;
        var command = text.Split(' ')[0];

        if (text == "🛍️ Каталог") command = "/catalog";
        else if (text == "🛒 Корзина") command = "/cart";
        else if (text == "📦 Мои заказы") command = "/myorders";
        else if (text == "📊 Склад") command = "/stock";
        else if (text == "🌐 Язык") command = "/lang";
        else if (text == "❓ Помощь") command = "/help";
        else if (text == "ℹ️ О магазине") command = "/about";

        switch (command)
        {
            case "/start": await Start(chatId, lang); break;
            case "/catalog": await Catalog(chatId, lang); break;
            case "/cart": await Cart(chatId, lang); break;
            case "/add": await AddToCart(chatId, text, lang); break;
            case "/remove": await RemoveFromCart(chatId, text, lang); break;
            case "/checkout": await Checkout(chatId, lang); break;
            case "/myorders": await MyOrders(chatId, lang); break;
            case "/stock": await Stock(chatId, lang); break;
            case "/track": await TrackOrder(chatId, text, lang); break;
            case "/lang": await LanguageMenu(chatId); break;
            case "/about": await About(chatId, lang); break;
            case "/help": await Help(chatId, lang); break;
            case "/status":
                if (chatId.ToString() == config!["AdminChatId"])
                {
                    await UpdateOrderStatus(chatId, text, lang);
                }
                break;
            default: await SendHtmlMessage(chatId, "❓ <b>Commande inconnue</b>\nUtilisez /help"); break;
        }
    }

    static async Task<string> GetUserLanguage(long chatId)
    {
        if (userLanguages.ContainsKey(chatId))
            return userLanguages[chatId];

        var lang = db!.GetUserLanguage(chatId);
        userLanguages[chatId] = lang;
        return lang;
    }

    static async Task Start(long chatId, string lang)
    {
        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("🛍️ Каталог"), new KeyboardButton("🛒 Корзина") },
            new[] { new KeyboardButton("📦 Мои заказы"), new KeyboardButton("📊 Склад") },
            new[] { new KeyboardButton("ℹ️ О магазине"), new KeyboardButton("🌐 Язык") },
            new[] { new KeyboardButton("❓ Помощь") }
        })
        { ResizeKeyboard = true };

        var welcomeMessage =
            "🤖 <b>ДОБРО ПОЖАЛОВАТЬ В TECH SHOP PRO</b> 🤖\n\n" +
            "<b>🖥️</b> Компьютерная техника и комплектующие\n" +
            "<b>🚚</b> Быстрая доставка по всей России\n" +
            "<b>💳</b> Оплата при получении или картой\n" +
            "<b>⭐</b> <i>Гарантия качества 12 месяцев</i>\n\n" +
            "<i>👇 Выберите действие на клавиатуре 👇</i>";

        await bot!.SendTextMessageAsync(chatId, welcomeMessage, parseMode: ParseMode.Html, replyMarkup: keyboard);
    }

    static async Task Catalog(long chatId, string lang)
    {
        await CatalogPage(chatId, 0, lang);
    }

    static async Task CatalogPage(long chatId, int page, string lang)
    {
        var dt = db!.GetCatalog();
        var products = dt.AsEnumerable().ToList();
        var pageSize = 5;
        var totalPages = (int)Math.Ceiling((double)products.Count / pageSize);

        if (page < 0) page = 0;
        if (page >= totalPages) page = totalPages - 1;

        var msg = "<b>🛍️ КАТАЛОГ ТОВАРОВ</b>\n\n";

        for (int i = page * pageSize; i < Math.Min((page + 1) * pageSize, products.Count); i++)
        {
            var row = products[i];
            var stock = Convert.ToInt32(row["Stock"]);
            var price = Convert.ToDecimal(row["prix"]);
            var stockEmoji = stock > 0 ? "✅" : "❌";
            var stockText = stock > 0 ? $"В наличии: <b>{stock}</b> шт." : "<i>Нет в наличии</i>";

            msg += $"┌────────────────────────────────────┐\n";
            msg += $"│ <b>{row["id_prod"]}. {row["Nom"]}</b>\n";
            msg += $"├────────────────────────────────────┤\n";
            msg += $"│ 💰 Цена: <b>{price:N0} ₽</b>\n";
            msg += $"│ {stockEmoji} {stockText}\n";
            msg += $"└────────────────────────────────────┘\n\n";
        }

        msg += $"<i>📄 Страница {page + 1} из {totalPages}</i>";

        var buttons = new List<List<InlineKeyboardButton>>();
        var navButtons = new List<InlineKeyboardButton>();

        if (page > 0)
            navButtons.Add(InlineKeyboardButton.WithCallbackData("◀️ Назад", $"page_{page - 1}"));

        if (page + 1 < totalPages)
            navButtons.Add(InlineKeyboardButton.WithCallbackData("Вперед ▶️", $"page_{page + 1}"));

        if (navButtons.Count > 0)
            buttons.Add(navButtons);

        for (int i = page * pageSize; i < Math.Min((page + 1) * pageSize, products.Count); i++)
        {
            var row = products[i];
            var stock = Convert.ToInt32(row["Stock"]);
            if (stock > 0)
            {
                buttons.Add(new List<InlineKeyboardButton>
                {
                    InlineKeyboardButton.WithCallbackData($"🛒 В корзину - {row["Nom"]}", $"add_{row["id_prod"]}")
                });
            }
        }

        buttons.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("🛒 Моя корзина", "show_cart"),
            InlineKeyboardButton.WithCallbackData("✅ Оформить заказ", "show_checkout")
        });

        var keyboard = new InlineKeyboardMarkup(buttons);
        await bot!.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Html, replyMarkup: keyboard);
    }

    static async Task Cart(long chatId, string lang)
    {
        var dt = db!.GetCart(chatId);

        if (dt.Rows.Count == 0)
        {
            await SendHtmlMessage(chatId, "🛒 <b>Корзина пуста</b>\n\nДобавьте товары через /catalog");
            return;
        }

        var msg = "<b>🛒 КОРЗИНА</b>\n\n";
        decimal total = 0;
        int num = 1;

        foreach (DataRow row in dt.Rows)
        {
            var lineTotal = Convert.ToDecimal(row["TotalLigne"]);
            var qty = Convert.ToInt32(row["quantite"]);
            var price = Convert.ToDecimal(row["prix"]);

            msg += $"┌────────────────────────────────────┐\n";
            msg += $"│ <b>{num}. {row["Nom"]}</b>\n";
            msg += $"├────────────────────────────────────┤\n";
            msg += $"│ 📦 Количество: <b>{qty}</b>\n";
            msg += $"│ 💰 Цена: <b>{price:N0} ₽</b>\n";
            msg += $"│ 💵 Сумма: <b>{lineTotal:N0} ₽</b>\n";
            msg += $"│ 🗑️ /remove {row["id_prod"]}\n";
            msg += $"└────────────────────────────────────┘\n\n";

            total += lineTotal;
            num++;
        }

        msg += $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n";
        msg += $"💵 <b>ИТОГО: {total:N0} ₽</b>\n";
        msg += $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n";
        msg += $"✅ /checkout - <i>Оформить заказ</i>";

        await bot!.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Html);
    }

    static async Task AddToCart(long chatId, string text, string lang)
    {
        var parts = text.Split(' ');
        if (parts.Length < 3)
        {
            await SendHtmlMessage(chatId, "❌ <b>Ошибка</b>\nИспользование: <code>/add &lt;ID&gt; &lt;количество&gt;</code>\nПример: /add 1 2");
            return;
        }

        if (!int.TryParse(parts[1], out int prodId) || !int.TryParse(parts[2], out int qty))
        {
            await SendHtmlMessage(chatId, "❌ <b>Ошибка</b>\nНеверный формат. Пример: <code>/add 1 2</code>");
            return;
        }

        if (db!.AddToCart(chatId, prodId, qty))
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🛒 В корзину", "show_cart") },
                new[] { InlineKeyboardButton.WithCallbackData("✅ Оформить заказ", "show_checkout") }
            });

            await SendHtmlMessage(chatId, $"✅ <b>Товар #{prodId} добавлен в корзину!</b>", keyboard);
        }
        else
        {
            await SendHtmlMessage(chatId, "❌ <b>Ошибка</b>\nТовар не найден или нет в наличии");
        }
    }

    static async Task RemoveFromCart(long chatId, string text, string lang)
    {
        var parts = text.Split(' ');
        if (parts.Length < 2) return;

        if (int.TryParse(parts[1], out int prodId))
        {
            db!.RemoveFromCart(chatId, prodId);
            await SendHtmlMessage(chatId, "🗑️ <b>Товар удалён из корзины</b>");
            await Cart(chatId, lang);
        }
    }

    static async Task Checkout(long chatId, string lang)
    {
        var dt = db!.GetCart(chatId);
        if (dt.Rows.Count == 0)
        {
            await SendHtmlMessage(chatId, "🛒 <b>Корзина пуста</b>\n\nДобавьте товары через /catalog");
            return;
        }

        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("📱 Поделиться номером") { RequestContact = true } }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };

        userStates[chatId] = "waiting_phone";

        await SendHtmlMessage(chatId,
            "<b>📞 ОФОРМЛЕНИЕ ЗАКАЗА</b>\n\n" +
            "Пожалуйста, поделитесь вашим номером телефона для доставки.\n\n" +
            "<i>👇 Нажмите на кнопку ниже</i>",
            keyboard);
    }

    static async Task ShowOrderConfirmation(long chatId, string address, string lang)
    {
        userStates.Remove(chatId);

        var cart = db!.GetCart(chatId);
        decimal total = 0;
        var items = new List<string>();

        foreach (DataRow row in cart.Rows)
        {
            var lineTotal = Convert.ToDecimal(row["TotalLigne"]);
            var qty = Convert.ToInt32(row["quantite"]);
            var name = row["Nom"].ToString();
            items.Add($"• {name} x{qty} = <b>{lineTotal:N0} ₽</b>");
            total += lineTotal;
        }

        var phone = db!.GetUserPhone(chatId);

        var msg =
            "<b>📋 ПОДТВЕРЖДЕНИЕ ЗАКАЗА</b>\n\n" +
            $"📍 <b>Адрес:</b> {address}\n" +
            $"📞 <b>Телефон:</b> {phone}\n\n" +
            "<b>Состав заказа:</b>\n" +
            string.Join("\n", items) +
            $"\n\n💵 <b>ИТОГО: {total:N0} ₽</b>\n\n" +
            "<i>✅ Подтвердите заказ:</i>";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ ПОДТВЕРДИТЬ", "confirm_order"),
                InlineKeyboardButton.WithCallbackData("❌ ОТМЕНА", "cancel_order")
            }
        });

        var orderId = db!.CreateOrder(chatId, address);
        if (orderId > 0)
        {
            pendingOrders[chatId] = orderId;
            await bot!.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Html, replyMarkup: keyboard);
        }
        else
        {
            await SendHtmlMessage(chatId, "❌ <b>Ошибка создания заказа</b>\nПопробуйте позже.");
        }
    }

    static async Task ProcessFinalOrder(long chatId, int orderId, string lang)
    {
        var orderDetails = db!.GetOrderDetails(orderId);
        decimal total = 0;
        var recap = "<b>📋 РЕКАПИТУЛЯЦИЯ ЗАКАЗА:</b>\n";

        foreach (DataRow row in orderDetails.Rows)
        {
            var lineTotal = Convert.ToDecimal(row["TotalLigne"]);
            total += lineTotal;
            recap += $"   • {row["Produit"]} x{row["quantite"]} = <b>{lineTotal:N0} ₽</b>\n";
        }

        var deliveryDate = DateTime.Now.AddDays(3).ToString("dd.MM.yyyy");

        var msg =
            "✅ <b>ЗАКАЗ ОФОРМЛЕН!</b>\n\n" +
            $"🆔 <b>Номер заказа:</b> #{orderId}\n" +
            recap + "\n" +
            $"💵 <b>ИТОГО: {total:N0} ₽</b>\n" +
            $"💳 <b>Оплата:</b> при получении\n" +
            $"🚚 <b>Доставка:</b> 2-5 рабочих дней\n" +
            $"📅 <b>Ожидаемая дата:</b> {deliveryDate}\n\n" +
            $"📞 <i>Отследить заказ: /track {orderId}</i>\n" +
            "🛍️ <i>Продолжить покупки: /catalog</i>";

        await bot!.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Html);

        await NotifyAdmin(orderId, chatId);
    }

    static async Task MyOrders(long chatId, string lang)
    {
        var dt = db!.GetMyOrders(chatId);

        if (dt.Rows.Count == 0)
        {
            await SendHtmlMessage(chatId, "📭 <b>У вас пока нет заказов</b>\n\n/catalog - Перейти в каталог");
            return;
        }

        var msg = "<b>📦 МОИ ЗАКАЗЫ</b>\n\n";
        foreach (DataRow row in dt.Rows)
        {
            var status = row["statut"]?.ToString() ?? "en attente";
            var statusIcon = status == "en attente" ? "⏳" : status == "payée" ? "✅" : status == "expédiée" ? "🚚" : "📦";
            var statusText = status == "en attente" ? "Ожидает" : status == "payée" ? "Оплачен" : status == "expédiée" ? "В пути" : "Доставлен";

            msg += $"┌────────────────────────────────────┐\n";
            msg += $"│ {statusIcon} <b>Заказ #{row["id_commande"]}</b>\n";
            msg += $"├────────────────────────────────────┤\n";
            msg += $"│ 📅 {Convert.ToDateTime(row["date_commande"]):dd.MM.yyyy}\n";
            msg += $"│ 💰 <b>{Convert.ToDecimal(row["total"]):N0} ₽</b>\n";
            msg += $"│ 📍 {row["adresse_livraison"]}\n";
            msg += $"│ 📊 <i>{statusText}</i>\n";
            msg += $"└────────────────────────────────────┘\n\n";
        }

        await bot!.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Html);
    }

    // =============================================
    // TRACK ORDER CORRIGÉE
    // =============================================
    static async Task TrackOrder(long chatId, string text, string lang)
    {
        var parts = text.Split(' ');
        if (parts.Length == 2 && int.TryParse(parts[1], out int orderId))
        {
            var orderDetails = db!.GetOrderDetails(orderId);
            if (orderDetails.Rows.Count == 0)
            {
                await SendHtmlMessage(chatId, "❌ <b>Заказ не найден</b>\nПроверьте номер заказа.");
                return;
            }

            var status = db!.GetOrderStatus(orderId);

            // Déterminer l'icône et le texte selon le statut
            string statusIcon, statusText, statusDescription;

            switch (status.ToLower())
            {
                case "en attente":
                    statusIcon = "⏳";
                    statusText = "Ожидает подтверждения";
                    statusDescription = "Ваш заказ принят, менеджер скоро свяжется с вами.";
                    break;
                case "payée":
                case "payee":
                    statusIcon = "✅";
                    statusText = "Оплачен";
                    statusDescription = "Заказ оплачен, готовится к отправке.";
                    break;
                case "preparation":
                    statusIcon = "🔧";
                    statusText = "Готовится к отправке";
                    statusDescription = "Заказ собирается на складе.";
                    break;
                case "expédiée":
                case "expediee":
                    statusIcon = "🚚";
                    statusText = "Отправлен";
                    statusDescription = "Заказ передан в службу доставки.";
                    break;
                case "livrée":
                case "livree":
                    statusIcon = "📦";
                    statusText = "Доставлен";
                    statusDescription = "Заказ доставлен. Благодарим за покупку!";
                    break;
                default:
                    statusIcon = "❓";
                    statusText = "Статус неизвестен";
                    statusDescription = "Обратитесь в поддержку.";
                    break;
            }

            // Calculer le total et récupérer les produits
            decimal total = 0;
            var items = new List<string>();
            foreach (DataRow row in orderDetails.Rows)
            {
                var lineTotal = Convert.ToDecimal(row["TotalLigne"]);
                total += lineTotal;
                items.Add($"   • {row["Produit"]} x{row["quantite"]} = {lineTotal:N0} ₽");
            }

            var deliveryDate = DateTime.Now.AddDays(3).ToString("dd.MM.yyyy");

            var msg =
                "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
                $"<b>📦 СТАТУС ЗАКАЗА #{orderId}</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
                $"{statusIcon} <b>Статус:</b> {statusText}\n" +
                $"📝 {statusDescription}\n\n" +
                "<b>📋 Состав заказа:</b>\n" +
                string.Join("\n", items) +
                $"\n\n💵 <b>Сумма:</b> {total:N0} ₽\n" +
                $"📅 <b>Ожидаемая доставка:</b> {deliveryDate}\n\n" +
                "❓ <i>Вопросы: /help</i>";

            await bot!.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Html);
        }
        else
        {
            await SendHtmlMessage(chatId,
                "❌ <b>Ошибка</b>\n" +
                "Использование: <code>/track &lt;ID_заказа&gt;</code>\n" +
                "Пример: <code>/track 9</code>\n\n" +
                "📋 <i>Чтобы узнать номер заказа, используйте /myorders</i>");
        }
    }

    static async Task Stock(long chatId, string lang)
    {
        var dt = db!.CheckStock();
        var msg = "<b>📊 ОСТАТКИ НА СКЛАДЕ</b>\n\n";

        var lowStock = dt.AsEnumerable().Where(r => r["Statut"].ToString()!.Contains("ALERTE")).ToList();

        if (lowStock.Any())
        {
            msg += "<i>⚠️ Товары с низким остатком:</i>\n";
            foreach (DataRow row in lowStock.Take(5))
            {
                msg += $"• {row["Produit"]}: <b>{row["quantite"]} шт.</b>\n";
            }
            msg += "\n";
        }

        msg += "<i>✅ Товары в наличии:</i>\n";
        var goodStock = dt.AsEnumerable().Where(r => !r["Statut"].ToString()!.Contains("ALERTE")).ToList();
        foreach (DataRow row in goodStock.Take(10))
        {
            msg += $"• {row["Produit"]}: <b>{row["quantite"]} шт.</b>\n";
        }

        if (goodStock.Count > 10)
            msg += $"\n<i>и ещё {goodStock.Count - 10} товаров...</i>";

        await bot!.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Html);
    }

    static async Task About(long chatId, string lang)
    {
        var msg =
            "<b>ℹ️ О МАГАЗИНЕ TECH SHOP PRO</b>\n\n" +
            "🖥️ <b>Компьютерная техника и комплектующие</b>\n" +
            "💻 Процессоры, видеокарты, материнские платы,\n" +
            "   оперативная память, SSD, периферия\n\n" +
            "<b>⭐ НАШИ ПРЕИМУЩЕСТВА:</b>\n" +
            "• ✅ Только оригинальная продукция\n" +
            "• ✅ Гарантия 12 месяцев\n" +
            "• ✅ Быстрая доставка\n" +
            "• ✅ Оплата при получении\n\n" +
            "<b>📞 КОНТАКТЫ:</b>\n" +
            "• Email: support@techshop.ru\n" +
            "• Тел: +7 (495) 123-45-67\n\n" +
            "<b>🕐 ВРЕМЯ РАБОТЫ:</b>\n" +
            "• Пн-Пт: 10:00 - 20:00\n" +
            "• Сб-Вс: 11:00 - 18:00";

        await bot!.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Html);
    }

    static async Task LanguageMenu(long chatId)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Русский", "lang_ru") },
            new[] { InlineKeyboardButton.WithCallbackData("🇫🇷 Français", "lang_fr") },
            new[] { InlineKeyboardButton.WithCallbackData("🇬🇧 English", "lang_en") }
        });

        await SendHtmlMessage(chatId, "🌐 <b>Выберите язык:</b>", keyboard);
    }

    static async Task Help(long chatId, string lang)
    {
        var msg =
            "<b>🆘 ПОМОЩЬ</b>\n\n" +
            "<b>📌 Основные команды:</b>\n" +
            "• /catalog - Просмотр каталога\n" +
            "• /cart - Корзина\n" +
            "• /add &lt;ID&gt; &lt;кол-во&gt; - Добавить товар\n" +
            "• /remove &lt;ID&gt; - Удалить товар\n" +
            "• /checkout - Оформить заказ\n" +
            "• /myorders - Мои заказы\n" +
            "• /stock - Остатки на складе\n" +
            "• /track &lt;ID&gt; - Отследить заказ\n" +
            "• /lang - Сменить язык\n" +
            "• /about - О магазине\n\n" +
            "<i>💡 Пример: /add 1 2 - добавить 2 шт. товара ID=1</i>\n\n" +
            "📞 <b>Поддержка:</b> @techshop_support";

        await bot!.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Html);
    }

    static async Task UpdateOrderStatus(long chatId, string text, string lang)
    {
        var parts = text.Split(' ');

        if (parts.Length < 3)
        {
            var recentOrders = db!.GetRecentOrdersForAdmin();
            var ordersList = "📋 <b>Commandes récentes:</b>\n";
            foreach (DataRow row in recentOrders.Rows)
            {
                ordersList += $"• #{row["id_commande"]} - {row["statut"]} - {Convert.ToDateTime(row["date_commande"]):dd.MM HH:mm}\n";
            }

            await SendHtmlMessage(chatId,
                "❌ <b>Ошибка</b>\n" +
                "Использование: <code>/status &lt;ID_заказа&gt; &lt;статус&gt;</code>\n\n" +
                "<b>Статусы:</b>\n" +
                "• payée - оплачен\n" +
                "• preparation - готовится\n" +
                "• expediee - отправлен\n" +
                "• livree - доставлен\n\n" +
                ordersList);
            return;
        }

        if (!int.TryParse(parts[1], out int orderId))
        {
            await SendHtmlMessage(chatId, "❌ <b>Ошибка</b>\nID заказа должен быть числом");
            return;
        }

        var newStatus = parts[2].ToLower();

        var validStatuses = new[] { "payée", "preparation", "expediee", "livree" };
        if (!validStatuses.Contains(newStatus))
        {
            await SendHtmlMessage(chatId, "❌ <b>Статус не найден</b>\nUtilisez: payée, preparation, expediee, livree");
            return;
        }

        if (db!.UpdateOrderStatus(orderId, newStatus))
        {
            var clientTelegramId = db!.GetTelegramIdByOrderId(orderId);
            if (clientTelegramId > 0)
            {
                db!.AddTrackingNotification(orderId, clientTelegramId, newStatus);
                await SendHtmlMessage(chatId, $"✅ <b>Статус заказа #{orderId} изменён на '{newStatus}'</b>\n\n📨 Уведомление отправлено клиенту.");
            }
            else
            {
                await SendHtmlMessage(chatId, $"✅ <b>Статус заказа #{orderId} изменён на '{newStatus}'</b>\n\n⚠️ Клиент не найден");
            }
        }
        else
        {
            await SendHtmlMessage(chatId, "❌ <b>Ошибка</b>\nЗаказ не найден");
        }
    }

    static async Task NotifyAdmin(int orderId, long customerId)
    {
        var adminToken = config!["AdminBotToken"];
        if (string.IsNullOrEmpty(adminToken)) return;

        var adminChatId = config!["AdminChatId"];
        if (string.IsNullOrEmpty(adminChatId)) return;

        try
        {
            var adminBot = new TelegramBotClient(adminToken);

            var msg =
                "<b>🆕 НОВЫЙ ЗАКАЗ!</b>\n\n" +
                $"🆔 <b>Заказ:</b> #{orderId}\n" +
                $"👤 <b>Клиент:</b> {customerId}\n" +
                $"📅 <b>Дата:</b> {DateTime.Now:dd.MM.yyyy HH:mm}\n\n" +
                "<i>✅ Требуется обработка</i>";

            await adminBot.SendTextMessageAsync(adminChatId, msg, parseMode: ParseMode.Html);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Admin notification error: {ex.Message}");
        }
    }

    static async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        if (callback.Data is null) return;

        if (callback.Data.StartsWith("lang_"))
        {
            var lang = callback.Data.Substring(5);
            db!.SetLanguage(callback.From.Id, lang);
            userLanguages[callback.From.Id] = lang;
            await SendHtmlMessage(callback.Message!.Chat.Id, "🌐 <b>Язык изменён</b>");
            await Start(callback.Message.Chat.Id, lang);
            await bot.AnswerCallbackQueryAsync(callback.Id);
        }
        else if (callback.Data.StartsWith("add_"))
        {
            var prodId = int.Parse(callback.Data.Split('_')[1]);
            var chatId = callback.Message!.Chat.Id;

            if (db!.AddToCart(chatId, prodId, 1))
            {
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🛒 В корзину", "show_cart") },
                    new[] { InlineKeyboardButton.WithCallbackData("✅ Оформить заказ", "show_checkout") }
                });

                await SendHtmlMessage(chatId, $"✅ <b>Товар #{prodId} добавлен в корзину!</b>", keyboard);
                await bot.AnswerCallbackQueryAsync(callback.Id, "✅ Добавлено!");
            }
            else
            {
                await bot.AnswerCallbackQueryAsync(callback.Id, "❌ Товар недоступен!");
            }
        }
        else if (callback.Data == "confirm_order" && pendingOrders.ContainsKey(callback.Message!.Chat.Id))
        {
            var chatId = callback.Message.Chat.Id;
            var orderId = pendingOrders[chatId];
            var lang = await GetUserLanguage(chatId);
            await ProcessFinalOrder(chatId, orderId, lang);
            pendingOrders.Remove(chatId);
            userStates.Remove(chatId);
            await bot.AnswerCallbackQueryAsync(callback.Id, "✅ Заказ подтверждён!");
        }
        else if (callback.Data == "cancel_order")
        {
            var chatId = callback.Message!.Chat.Id;
            pendingOrders.Remove(chatId);
            userStates.Remove(chatId);
            await SendHtmlMessage(chatId, "❌ <b>Заказ отменён</b>");
            await bot.AnswerCallbackQueryAsync(callback.Id, "❌ Отменено");
        }
        else if (callback.Data == "show_cart")
        {
            var chatId = callback.Message!.Chat.Id;
            var lang = await GetUserLanguage(chatId);
            await Cart(chatId, lang);
            await bot.AnswerCallbackQueryAsync(callback.Id);
        }
        else if (callback.Data == "show_checkout")
        {
            var chatId = callback.Message!.Chat.Id;
            var lang = await GetUserLanguage(chatId);
            await Checkout(chatId, lang);
            await bot.AnswerCallbackQueryAsync(callback.Id);
        }
        else if (callback.Data == "back_to_catalog")
        {
            var chatId = callback.Message!.Chat.Id;
            var lang = await GetUserLanguage(chatId);
            await Catalog(chatId, lang);
            await bot.AnswerCallbackQueryAsync(callback.Id);
        }
        else if (callback.Data.StartsWith("page_"))
        {
            var page = int.Parse(callback.Data.Split('_')[1]);
            var chatId = callback.Message!.Chat.Id;
            var lang = await GetUserLanguage(chatId);
            await CatalogPage(chatId, page, lang);
            await bot.AnswerCallbackQueryAsync(callback.Id);
        }
    }

    static Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        Console.WriteLine($"❌ Erreur: {ex.Message}");
        return Task.CompletedTask;
    }
}