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
//  Sie löscht und ändert nie etwas (keine Typänderungen, keine Umbenennungen). In
//  solchen Fällen hilft weiterhin nur: vertretung.db löschen und neu seeden.
//
//  Phase 5.2b (Entscheidung B): fehlende NICHT eindeutige Indizes auf vorhandenen
//  Tabellen werden ergänzt (kann nie an vorhandenen Daten scheitern); fehlende
//  eindeutige Indizes nur gemeldet. Einzige Ausnahme vom „löscht nie“: Indizes aus
//  <see cref="ErsetzteIndizes"/> – hier der eindeutige Index der Zugehörigkeiten, der
//  mit der Gültigkeit von–bis durch einen nicht eindeutigen ersetzt wurde.
//
//  Schalter: "Database:SchemaNachruesten" (Standard true). Produktiv später
//  EF-Migrations verwenden und den Schalter auf false setzen.
// =====================================================================

public static class SchemaNachruestung
{
    /// <summary>
    /// Indizes, die das Modell nicht mehr kennt und die stören würden (Phase 5.2b): werden in einer
    /// vorhandenen Entwicklungs-DB entfernt. Nur Indizes – nie Tabellen oder Spalten.
    /// </summary>
    private static readonly string[] ErsetzteIndizes =
    {
        "IX_Zugehoerigkeiten_SchuelerId_LerngruppeId_Schuljahr"   // eindeutig bis 5.2b-1
    };

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

        // Phase 5.2b: Indizes. Zuerst ersetzte Indizes entfernen, dann fehlende ergänzen.
        var indizes = VorhandeneIndizes(db) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sollIndizes = soll.OfType<CreateIndexOperation>().Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entfernen = new List<MigrationOperation>();
        foreach (var name in ErsetzteIndizes)
        {
            if (!indizes.Contains(name) || sollIndizes.Contains(name)) continue;
            string tabelle = name.Split('_')[1];
            entfernen.Add(new DropIndexOperation { Name = name, Table = tabelle });
            indizes.Remove(name);
            meldungen.Add($"Index {name} entfernt (ersetzt)");
        }
        ops.InsertRange(0, entfernen);

        foreach (var index in soll.OfType<CreateIndexOperation>())
        {
            if (index.Table == null) continue;
            if (neueTabellen.Contains(index.Table)) { ops.Add(index); continue; }
            if (indizes.Contains(index.Name)) continue;
            if (index.IsUnique)
            {
                meldungen.Add($"Eindeutiger Index {index.Name} fehlt – nicht nachgerüstet (vertretung.db neu anlegen, falls nötig)");
                continue;
            }
            ops.Add(index);
            meldungen.Add($"Index {index.Name} ergänzt");
        }

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

    /// <summary>Namen der vorhandenen Indizes (null: Provider unbekannt).</summary>
    private static HashSet<string>? VorhandeneIndizes(AppDbContext db)
    {
        string sql;
        if (db.Database.IsSqlite())
            sql = "SELECT name FROM sqlite_master WHERE type = 'index' AND name IS NOT NULL";
        else if (db.Database.IsNpgsql())
            sql = "SELECT indexname FROM pg_indexes WHERE schemaname = current_schema()";
        else
            return null;

        var ergebnis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = db.Database.GetDbConnection();
        bool warOffen = conn.State == ConnectionState.Open;
        if (!warOffen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0)) ergebnis.Add(r.GetString(0));
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
