using Microsoft.Data.SqlClient;
using System.Data;

public class Database
{
    private string _conn;

    public Database(string connectionString)
    {
        _conn = connectionString;
    }

    public void TestConnection()
    {
        try
        {
            using var c = new SqlConnection(_conn);
            c.Open();
            Console.WriteLine("✅ Base de données connectée !");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erreur DB: {ex.Message}");
        }
    }

    public string GetUserLanguage(long telegramId)
    {
        using var c = new SqlConnection(_conn);
        var query = "SELECT langue_preferee FROM Clients WHERE telegram_id = @tid";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@tid", telegramId);
        c.Open();
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "ru";
    }

    public void SetLanguage(long telegramId, string lang)
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            IF EXISTS (SELECT 1 FROM Clients WHERE telegram_id = @tid)
                UPDATE Clients SET langue_preferee = @lang WHERE telegram_id = @tid
            ELSE
                INSERT INTO Clients (nom, telegram_id, langue_preferee) 
                VALUES ('Utilisateur', @tid, @lang)";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@tid", telegramId);
        cmd.Parameters.AddWithValue("@lang", lang);
        c.Open();
        cmd.ExecuteNonQuery();
    }

    public void SaveUserPhone(long telegramId, string phone)
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            IF EXISTS (SELECT 1 FROM Clients WHERE telegram_id = @tid)
                UPDATE Clients SET telephone = @phone WHERE telegram_id = @tid
            ELSE
                INSERT INTO Clients (nom, telegram_id, telephone, langue_preferee) 
                VALUES ('Utilisateur', @tid, @phone, 'ru')";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@tid", telegramId);
        cmd.Parameters.AddWithValue("@phone", phone);
        c.Open();
        cmd.ExecuteNonQuery();
    }

    public string GetUserPhone(long telegramId)
    {
        using var c = new SqlConnection(_conn);
        var query = "SELECT telephone FROM Clients WHERE telegram_id = @tid";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@tid", telegramId);
        c.Open();
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "Non renseigné";
    }

    public DataTable GetCatalog()
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            SELECT p.id_prod, p.nom_ru AS Nom, p.prix, 
                   ISNULL(s.quantite, 0) AS Stock
            FROM Produits p
            LEFT JOIN Stock s ON p.id_prod = s.id_prod
            ORDER BY p.id_prod";
        using var a = new SqlDataAdapter(query, c);
        var dt = new DataTable();
        a.Fill(dt);
        return dt;
    }

    public DataTable GetCart(long telegramId)
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            SELECT p.id_prod, p.nom_ru AS Nom, pt.quantite, p.prix, 
                   (pt.quantite * p.prix) AS TotalLigne
            FROM PanierTemp pt
            JOIN Produits p ON pt.id_prod = p.id_prod
            WHERE pt.telegram_id = @tid";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@tid", telegramId);
        var da = new SqlDataAdapter(cmd);
        var dt = new DataTable();
        da.Fill(dt);
        return dt;
    }

    public bool AddToCart(long telegramId, int prodId, int qty)
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            IF EXISTS (SELECT 1 FROM PanierTemp WHERE telegram_id = @tid AND id_prod = @pid)
                UPDATE PanierTemp SET quantite = quantite + @qty WHERE telegram_id = @tid AND id_prod = @pid
            ELSE
                INSERT INTO PanierTemp (telegram_id, id_prod, quantite) 
                VALUES (@tid, @pid, @qty)";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@tid", telegramId);
        cmd.Parameters.AddWithValue("@pid", prodId);
        cmd.Parameters.AddWithValue("@qty", qty);
        c.Open();
        return cmd.ExecuteNonQuery() > 0;
    }

    public void RemoveFromCart(long telegramId, int prodId)
    {
        using var c = new SqlConnection(_conn);
        var query = "DELETE FROM PanierTemp WHERE telegram_id = @tid AND id_prod = @pid";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@tid", telegramId);
        cmd.Parameters.AddWithValue("@pid", prodId);
        c.Open();
        cmd.ExecuteNonQuery();
    }

    public int CreateOrder(long telegramId, string address)
    {
        using var c = new SqlConnection(_conn);
        c.Open();
        using var tx = c.BeginTransaction();

        try
        {
            var cmd = new SqlCommand("SELECT id_client FROM Clients WHERE telegram_id = @tid", c, tx);
            cmd.Parameters.AddWithValue("@tid", telegramId);
            var clientId = cmd.ExecuteScalar();
            if (clientId == null) return -1;

            cmd = new SqlCommand(@"
                SELECT ISNULL(SUM(p.prix * pt.quantite), 0)
                FROM PanierTemp pt
                INNER JOIN Produits p ON pt.id_prod = p.id_prod
                WHERE pt.telegram_id = @tid", c, tx);
            cmd.Parameters.AddWithValue("@tid", telegramId);
            var total = Convert.ToDecimal(cmd.ExecuteScalar());

            Console.WriteLine($"📊 Création commande - Client: {clientId}, Total: {total}");

            if (total <= 0) return -2;

            cmd = new SqlCommand(@"
                INSERT INTO Commandes (id_client, total, adresse_livraison, statut, date_commande)
                VALUES (@cid, @total, @addr, 'en attente', GETDATE());
                SELECT SCOPE_IDENTITY();", c, tx);
            cmd.Parameters.AddWithValue("@cid", clientId);
            cmd.Parameters.AddWithValue("@total", total);
            cmd.Parameters.AddWithValue("@addr", address);
            var orderId = Convert.ToInt32(cmd.ExecuteScalar());

            Console.WriteLine($"✅ Commande #{orderId} créée avec total: {total}");

            cmd = new SqlCommand(@"
                INSERT INTO LignesCommande (id_commande, id_prod, quantite, prix_unitaire)
                SELECT @oid, pt.id_prod, pt.quantite, p.prix
                FROM PanierTemp pt
                INNER JOIN Produits p ON pt.id_prod = p.id_prod
                WHERE pt.telegram_id = @tid", c, tx);
            cmd.Parameters.AddWithValue("@oid", orderId);
            cmd.Parameters.AddWithValue("@tid", telegramId);
            cmd.ExecuteNonQuery();

            cmd = new SqlCommand(@"
                UPDATE s 
                SET s.quantite = s.quantite - pt.quantite
                FROM Stock s
                INNER JOIN PanierTemp pt ON s.id_prod = pt.id_prod
                WHERE pt.telegram_id = @tid", c, tx);
            cmd.Parameters.AddWithValue("@tid", telegramId);
            cmd.ExecuteNonQuery();

            cmd = new SqlCommand("DELETE FROM PanierTemp WHERE telegram_id = @tid", c, tx);
            cmd.Parameters.AddWithValue("@tid", telegramId);
            cmd.ExecuteNonQuery();

            tx.Commit();

            AddTrackingNotification(orderId, telegramId, "order_created");

            return orderId;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            Console.WriteLine($"❌ Erreur CreateOrder: {ex.Message}");
            return -1;
        }
    }

    // =============================================
    // MÉTHODES POUR LES NOTIFICATIONS
    // =============================================

    public DataTable GetPendingNotifications()
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            SELECT n.id_notification, n.order_id, n.telegram_id, n.type, n.status,
                   c.total, c.adresse_livraison
            FROM Notifications n
            JOIN Commandes c ON n.order_id = c.id_commande
            WHERE n.sent_at IS NULL
            ORDER BY n.created_at ASC";
        using var da = new SqlDataAdapter(query, c);
        var dt = new DataTable();
        da.Fill(dt);
        return dt;
    }

    public void MarkNotificationSent(int notificationId)
    {
        using var c = new SqlConnection(_conn);
        var query = "UPDATE Notifications SET sent_at = GETDATE() WHERE id_notification = @id";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@id", notificationId);
        c.Open();
        cmd.ExecuteNonQuery();
    }

    public bool UpdateOrderStatus(int orderId, string newStatus)
    {
        using var c = new SqlConnection(_conn);
        var query = "UPDATE Commandes SET statut = @status WHERE id_commande = @oid";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@status", newStatus);
        cmd.Parameters.AddWithValue("@oid", orderId);
        c.Open();
        return cmd.ExecuteNonQuery() > 0;
    }

    public void AddTrackingNotification(int orderId, long telegramId, string status)
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            INSERT INTO Notifications (order_id, telegram_id, type, status, created_at)
            VALUES (@oid, @tid, 'status_update', @status, GETDATE())";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@oid", orderId);
        cmd.Parameters.AddWithValue("@tid", telegramId);
        cmd.Parameters.AddWithValue("@status", status);
        c.Open();
        cmd.ExecuteNonQuery();
        Console.WriteLine($"📝 Notification ajoutée: Commande #{orderId}, Status: {status}");
    }

    public long GetTelegramIdByOrderId(int orderId)
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            SELECT c.telegram_id
            FROM Commandes cmd
            JOIN Clients c ON cmd.id_client = c.id_client
            WHERE cmd.id_commande = @oid";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@oid", orderId);
        c.Open();
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt64(result) : 0;
    }

    public DataTable GetRecentOrdersForAdmin(int limit = 10)
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            SELECT TOP (@limit) 
                id_commande, 
                statut, 
                date_commande,
                total
            FROM Commandes
            ORDER BY id_commande DESC";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@limit", limit);
        var da = new SqlDataAdapter(cmd);
        var dt = new DataTable();
        da.Fill(dt);
        return dt;
    }

    public DataTable GetOrderDetails(int orderId)
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            SELECT c.id_commande, c.date_commande, c.total, c.adresse_livraison, c.statut,
                   p.nom_ru AS Produit, lc.quantite, lc.prix_unitaire, 
                   (lc.quantite * lc.prix_unitaire) AS TotalLigne
            FROM Commandes c
            JOIN LignesCommande lc ON c.id_commande = lc.id_commande
            JOIN Produits p ON lc.id_prod = p.id_prod
            WHERE c.id_commande = @oid";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@oid", orderId);
        var da = new SqlDataAdapter(cmd);
        var dt = new DataTable();
        da.Fill(dt);
        return dt;
    }

    public DataTable GetMyOrders(long telegramId)
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            SELECT c.id_commande, c.date_commande, c.total, c.adresse_livraison, c.statut
            FROM Commandes c
            JOIN Clients cl ON c.id_client = cl.id_client
            WHERE cl.telegram_id = @tid
            ORDER BY c.id_commande DESC";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@tid", telegramId);
        var da = new SqlDataAdapter(cmd);
        var dt = new DataTable();
        da.Fill(dt);
        return dt;
    }

    public DataTable CheckStock()
    {
        using var c = new SqlConnection(_conn);
        var query = @"
            SELECT p.id_prod, p.nom_ru AS Produit, s.quantite,
                   CASE WHEN s.quantite <= 5 THEN '⚠️ ALERTE' ELSE '✅ OK' END AS Statut
            FROM Produits p
            JOIN Stock s ON p.id_prod = s.id_prod
            ORDER BY s.quantite ASC";
        using var da = new SqlDataAdapter(query, c);
        var dt = new DataTable();
        da.Fill(dt);
        return dt;
    }

    public string GetOrderStatus(int orderId)
    {
        using var c = new SqlConnection(_conn);
        var query = "SELECT statut FROM Commandes WHERE id_commande = @oid";
        using var cmd = new SqlCommand(query, c);
        cmd.Parameters.AddWithValue("@oid", orderId);
        c.Open();
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "inconnu";
    }

    public string CreatePaymentLink(int orderId, decimal amount, string currency = "RUB")
    {
        return $"https://payment.test/pay?order={orderId}&amount={amount}&currency={currency}";
    }
}