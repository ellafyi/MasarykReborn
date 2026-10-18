namespace Masaryk.Postgres

open System.Data.Common
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes

/// One row of the IS course export.
type CatalogCourse =
    {
        Faculty: string
        Code: string
        /// `fall 2024` or `spring 2025`.
        Term: string
        Name: string
        /// The `Cíle` column, kept as HTML.
        Description: string option
    }

type CatalogChanges =
    {
        Imports: int
        Added: int
        Changed: int
        Removed: int
    }

/// A course with its name from the newest export that lists it.
type Course =
    {
        Id: int64
        Faculty: string
        Code: string
        Name: string
    }

type CourseRepository(dataSource: NpgsqlDataSource) =
    static let course (r: DbDataReader) =
        {
            Id = r.GetInt64 0
            Faculty = r.GetString 1
            Code = r.GetString 2
            Name = r.GetString 3
        }

    static let named =
        """
        SELECT DISTINCT ON (c.id) c.id, c.faculty_code, c.code, r.name
        FROM course c
        JOIN course_offering o ON o.course_id = c.id
        JOIN course_revision r ON r.offering_id = o.id AND r.present_in_export
        """

    /// Record one import per faculty and term in the export, with a revision for each course
    /// that is new, changed, back again, or no longer listed.
    member _.Import(source: string, courses: CatalogCourse list) : Task<CatalogChanges> =
        task {
            use! connection = dataSource.OpenConnectionAsync()
            use! transaction = connection.BeginTransactionAsync()

            let run (sql: string) =
                Sql.affected (new NpgsqlCommand(sql, connection, transaction))

            // Imports run one at a time, so each scope's newest import row is this one.
            let! _ = run "LOCK TABLE catalog_import IN EXCLUSIVE MODE" []

            let! _ =
                run
                    "CREATE TEMP TABLE incoming (faculty_code text, code text, term text, name text, description text) ON COMMIT DROP"
                    []

            do
                use writer =
                    connection.BeginBinaryImport("COPY incoming FROM STDIN (FORMAT BINARY)")

                for c in courses do
                    writer.StartRow()
                    writer.Write(c.Faculty, NpgsqlDbType.Text)
                    writer.Write(c.Code, NpgsqlDbType.Text)
                    writer.Write(c.Term, NpgsqlDbType.Text)
                    writer.Write(c.Name, NpgsqlDbType.Text)

                    match c.Description with
                    | Some description -> writer.Write(description, NpgsqlDbType.Text)
                    | None -> writer.WriteNull()

                writer.Complete() |> ignore

            let! _ =
                run "INSERT INTO faculty (code) SELECT DISTINCT faculty_code FROM incoming ON CONFLICT DO NOTHING" []

            let! _ =
                run "DELETE FROM incoming WHERE faculty_code IN (SELECT code FROM faculty WHERE NOT sync_enabled)" []

            let! imports =
                run
                    "INSERT INTO catalog_import (faculty_code, term, source_label) SELECT DISTINCT faculty_code, term, $1 FROM incoming"
                    [ box source ]

            let! _ =
                run
                    """
                    CREATE TEMP TABLE batch ON COMMIT DROP AS
                    SELECT DISTINCT ON (faculty_code, term) id, faculty_code, term FROM catalog_import
                    WHERE (faculty_code, term) IN (SELECT faculty_code, term FROM incoming)
                    ORDER BY faculty_code, term, id DESC
                    """
                    []

            let! _ =
                run
                    "INSERT INTO course (faculty_code, code) SELECT DISTINCT faculty_code, code FROM incoming ON CONFLICT DO NOTHING"
                    []

            let! _ =
                run
                    """
                    INSERT INTO course_offering (course_id, term)
                    SELECT DISTINCT c.id, i.term FROM incoming i
                    JOIN course c ON c.faculty_code = i.faculty_code AND c.code = i.code
                    ON CONFLICT DO NOTHING
                    """
                    []

            let! _ =
                run
                    """
                    CREATE TEMP TABLE listed ON COMMIT DROP AS
                    SELECT o.id AS offering_id, b.id AS import_id, i.name, i.description
                    FROM incoming i
                    JOIN course c ON c.faculty_code = i.faculty_code AND c.code = i.code
                    JOIN course_offering o ON o.course_id = c.id AND o.term = i.term
                    JOIN batch b ON b.faculty_code = i.faculty_code AND b.term = i.term
                    """
                    []

            let! _ =
                run
                    """
                    CREATE TEMP TABLE latest ON COMMIT DROP AS
                    SELECT DISTINCT ON (r.offering_id) r.offering_id, b.id AS import_id,
                           r.present_in_export, r.name, r.description
                    FROM course_revision r
                    JOIN course_offering o ON o.id = r.offering_id
                    JOIN course c ON c.id = o.course_id
                    JOIN batch b ON b.faculty_code = c.faculty_code AND b.term = o.term
                    ORDER BY r.offering_id, r.catalog_import_id DESC
                    """
                    []

            let revise =
                "INSERT INTO course_revision (offering_id, catalog_import_id, present_in_export, name, description)"

            let! added =
                run
                    $"""
                    {revise}
                    SELECT l.offering_id, l.import_id, true, l.name, l.description FROM listed l
                    WHERE NOT EXISTS (SELECT 1 FROM latest p WHERE p.offering_id = l.offering_id)
                    """
                    []

            let! changed =
                run
                    $"""
                    {revise}
                    SELECT l.offering_id, l.import_id, true, l.name, l.description
                    FROM listed l JOIN latest p ON p.offering_id = l.offering_id
                    WHERE NOT p.present_in_export
                       OR p.name IS DISTINCT FROM l.name
                       OR p.description IS DISTINCT FROM l.description
                    """
                    []

            let! removed =
                run
                    $"""
                    {revise}
                    SELECT p.offering_id, p.import_id, false, NULL, NULL FROM latest p
                    WHERE p.present_in_export
                      AND NOT EXISTS (SELECT 1 FROM listed l WHERE l.offering_id = p.offering_id)
                    """
                    []

            do! transaction.CommitAsync()

            return
                {
                    Imports = imports
                    Added = added
                    Changed = changed
                    Removed = removed
                }
        }

    /// Find a course by faculty and code, ignoring case.
    member _.Find(faculty: string, code: string) : Task<Course option> =
        Sql.queryOne
            (dataSource.CreateCommand
                $"""
                {named}
                WHERE lower(c.faculty_code) = lower($1) AND lower(c.code) = lower($2)
                ORDER BY c.id, r.catalog_import_id DESC
                """)
            [ box faculty; box code ]
            course

    /// Up to 25 courses whose code starts with text or whose name contains it, home faculty first.
    member _.Search(text: string, home: string) : Task<Course list> =
        Sql.query
            (dataSource.CreateCommand
                $"""
                SELECT * FROM (
                    {named}
                    WHERE c.code ILIKE $1 || '%%' OR r.name ILIKE '%%' || $1 || '%%'
                    ORDER BY c.id, r.catalog_import_id DESC
                ) found
                ORDER BY faculty_code <> $2, code NOT ILIKE $1 || '%%', code
                LIMIT 25
                """)
            [ box text; box home ]
            course

    member _.All() : Task<Course list> =
        Sql.query (dataSource.CreateCommand $"{named} ORDER BY c.id, r.catalog_import_id DESC") [] course

    /// One page of courses ordered by faculty and code, with linked channels and the total count.
    member _.Page(faculty: string option, offset: int, limit: int) : Task<(Course * uint64 option) list * int64> =
        task {
            let! rows =
                Sql.query
                    (dataSource.CreateCommand
                        $"""
                        SELECT f.*, l.channel_id, count(*) OVER () FROM (
                            {named}
                            WHERE $1::text IS NULL OR lower(c.faculty_code) = lower($1)
                            ORDER BY c.id, r.catalog_import_id DESC
                        ) f
                        LEFT JOIN course_channel l ON l.course_id = f.id
                        ORDER BY f.faculty_code, f.code
                        LIMIT $2 OFFSET $3
                        """)
                    [ Sql.optional faculty; box limit; box offset ]
                    (fun r -> (course r, Sql.readOptional r 4 (r.GetDecimal >> uint64)), r.GetInt64 5)

            let total = rows |> List.tryHead |> Option.map snd |> Option.defaultValue 0L
            return List.map fst rows, total
        }

    member _.Faculties() : Task<string list> =
        Sql.query (dataSource.CreateCommand "SELECT code FROM faculty ORDER BY code") [] _.GetString(0)

    /// Course ID and channel ID of every linked course.
    member _.Links() : Task<(int64 * uint64) list> =
        Sql.query (dataSource.CreateCommand "SELECT course_id, channel_id FROM course_channel") [] (fun r ->
            r.GetInt64 0, uint64 (r.GetDecimal 1))

    member _.ChannelOf(courseId: int64) : Task<uint64 option> =
        task {
            let! found =
                Sql.queryOne
                    (dataSource.CreateCommand "SELECT channel_id FROM course_channel WHERE course_id = $1")
                    [ box courseId ]
                    _.GetDecimal(0)

            return found |> Option.map uint64
        }

    /// Link a course to a channel, replacing any other link either of them has.
    member _.Link(courseId: int64, channelId: uint64) : Task<unit> =
        task {
            use! connection = dataSource.OpenConnectionAsync()
            use! transaction = connection.BeginTransactionAsync()

            let command (sql: string) =
                new NpgsqlCommand(sql, connection, transaction)

            let values = [ box courseId; box (decimal channelId) ]

            do! Sql.execute (command "DELETE FROM course_channel WHERE course_id = $1 OR channel_id = $2") values
            do! Sql.execute (command "INSERT INTO course_channel (course_id, channel_id) VALUES ($1, $2)") values
            do! transaction.CommitAsync()
        }

    /// Returns whether the course had a link.
    member _.Unlink(courseId: int64) : Task<bool> =
        task {
            let! removed =
                Sql.affected
                    (dataSource.CreateCommand "DELETE FROM course_channel WHERE course_id = $1")
                    [ box courseId ]

            return removed > 0
        }

    /// The category an administrator assigned to this course or the longest matching code prefix.
    member _.CategoryOverride(courseId: int64, code: string) : Task<string option> =
        task {
            let! name =
                Sql.scalar
                    (dataSource.CreateCommand
                        """
                        SELECT coalesce(
                            (SELECT category_name FROM course_category_override WHERE course_id = $1),
                            (SELECT category_name FROM family_category_override
                             WHERE $2 ILIKE course_prefix || '%'
                             ORDER BY length(course_prefix) DESC LIMIT 1))
                        """)
                    [ box courseId; box code ]

            return
                match name with
                | :? string as name -> Some name
                | _ -> None
        }
