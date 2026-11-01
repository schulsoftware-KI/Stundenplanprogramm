using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace VertretungApi.Data;

// =====================================================================
//  Schema-Nachrüstung (Phase 2, Schritt 5) – nur für die Entwicklung.
//
//  EnsureCreated legt eine neue Datenbank vollständig an, ergänzt aber in einer
//  bestehenden nichts. Diese Klasse vergleicht beim Start das Modell mit der
//  vorhandenen Datenbank und ergänzt, was fehlt:
//    - fehlende Tabellen (samt Indizes),
//    - fehlende Spalten in vorhandenen Tabellen (Pflichtspalten mit Standardwert
//      0 / false / "" für vorhandene Zeilen).
//  Sie löscht und ändert nie etwas (keine Typänderungen, keine neuen Indizes auf
//  vorhandenen Tabellen, keine Umbenennungen). In solchen Fällen hilft weiterhin
//  nur: vertretung.db löschen und neu seeden.
//
//  Schalter: "Database:SchemaNachruesten" (Standard true). Produktiv später
//  EF-Migrations verwenden und den Schalter auf false setzen.
// =====================================================================

public static class SchemaNachruestung
{
    /// <summary>Ergänzt fehlende Tabellen/Spalten. Liefert, was ergänzt wurde.</summary>
    public static IReadOnlyList<string> Ausfuehren(AppDbContext db)
    {
        var meldungen = new List<string>();
        var vorhanden = VorhandeneSpalten(db);
        if (vorhanden == null || vorhanden.Count == 0) return meldungen; // unbekannter Provider bzw. leere DB

        var model = db.GetService<IDesignTimeModel>().Model;
        var differ = db.GetService<IMigrationsModelDiffer>();
        var soll = differ.GetDifferences(null, model.GetRelationalModel());

        var ops = new List<MigrationOperation>();
        var neueTabellen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tabelle in soll.OfType<CreateTableOperation>())
        {
            if (!vorhanden.TryGetValue(tabelle.Name, out var spalten))
            {
                ops.Add(tabelle);
                neueTabellen.Add(tabelle.Name);
                meldungen.Add($"Tabelle {tabelle.Name} angelegt");
                continue;
            }

            foreach (var spalte in tabelle.Columns)
            {
                if (spalten.Contains(spalte.Name)) continue;
                spalte.Table = tabelle.Name;
                spalte.Schema = tabelle.Schema;
                if (!spalte.IsNullable && spalte.DefaultValue == null && spalte.DefaultValueSql == null
                    && spalte.ComputedColumnSql == null)
                    spalte.DefaultValue = Standardwert(spalte.ClrType);
                ops.Add(spalte);
                meldungen.Add($"Spalte {tabelle.Name}.{spalte.Name} ergänzt");
            }
        }

        foreach (var index in soll.OfType<CreateIndexOperation>())
            if (index.Table != null && neueTabellen.Contains(index.Table)) ops.Add(index);

        if (ops.Count == 0) return meldungen;

        var befehle = db.GetService<IMigrationsSqlGenerator>().Generate(ops, db.Model);

        var conn = db.Database.GetDbConnection();
        bool warOffen = conn.State == ConnectionState.Open;
        if (!warOffen) conn.Open();
        try
        {
            using var tx = conn.BeginTransaction();
            foreach (var befehl in befehle)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = befehl.CommandText;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            if (!warOffen) conn.Close();
        }
        return meldungen;
    }

    /// <summary>Tabelle → Spaltennamen der vorhandenen Datenbank (null: Provider unbekannt).</summary>
    private static Dictionary<string, HashSet<string>>? VorhandeneSpalten(AppDbContext db)
    {
        string sql;
        if (db.Database.IsSqlite())
            sql = "SELECT m.name, p.name FROM sqlite_master m JOIN pragma_table_info(m.name) p " +
                  "WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'";
        else if (db.Database.IsNpgsql())
            sql = "SELECT table_name, column_name FROM information_schema.columns " +
                  "WHERE table_schema = current_schema()";
        else
            return null;

        var ergebnis = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var conn = db.Database.GetDbConnection();
        bool warOffen = conn.State == ConnectionState.Open;
        if (!warOffen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var tabelle = r.GetString(0);
                if (!ergebnis.TryGetValue(tabelle, out var spalten))
                    ergebnis[tabelle] = spalten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                spalten.Add(r.GetString(1));
            }
        }
        finally
        {
            if (!warOffen) conn.Close();
        }
        return ergebnis;
    }

    private static object? Standardwert(Type typ)
    {
        var t = Nullable.GetUnderlyingType(typ) ?? typ;
        if (t == typeof(string)) return "";
        if (t.IsEnum) return Convert.ChangeType(0, Enum.GetUnderlyingType(t));
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }
}
