namespace Masaryk.Postgres

open System.Data.Common
open System.Threading.Tasks
open Npgsql

/// One row of the IS course export.
type CatalogCourse =
    {
        Faculty: string
        Code: string
        Name: string
    }

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

    /// Add new courses and rename changed ones; returns how many were added and renamed.
    member _.Import(courses: CatalogCourse list) : Task<int * int> =
        task {
            let courses = courses |> List.distinctBy (fun c -> c.Faculty, c.Code)

            let column (pick: CatalogCourse -> string) =
                courses |> List.map pick |> List.toArray |> box

            let! inserted =
                Sql.query
                    (dataSource.CreateCommand
                        """
                        INSERT INTO course (faculty_code, code, name)
                        SELECT * FROM unnest($1::text[], $2::text[], $3::text[])
                        ON CONFLICT (faculty_code, code) DO UPDATE SET name = EXCLUDED.name
                        WHERE course.name <> EXCLUDED.name
                        RETURNING xmax = 0 -- true for a new row
                        """)
                    [ column _.Faculty; column _.Code; column _.Name ]
                    _.GetBoolean(0)

            return List.filter id inserted |> List.length, List.filter not inserted |> List.length
        }

    /// Find a course by faculty and code, ignoring case.
    member _.Find(faculty: string, code: string) : Task<Course option> =
        Sql.queryOne
            (dataSource.CreateCommand
                "SELECT id, faculty_code, code, name FROM course WHERE lower(faculty_code) = lower($1) AND lower(code) = lower($2)")
            [ box faculty; box code ]
            course

    /// Up to 25 courses whose code starts with text or whose name contains it, home faculty first.
    member _.Search(text: string, home: string) : Task<Course list> =
        Sql.query
            (dataSource.CreateCommand
                """
                SELECT id, faculty_code, code, name FROM course
                WHERE code ILIKE $1 || '%' OR name ILIKE '%' || $1 || '%'
                ORDER BY faculty_code <> $2, code NOT ILIKE $1 || '%', code
                LIMIT 25
                """)
            [ box text; box home ]
            course

    member _.All() : Task<Course list> =
        Sql.query (dataSource.CreateCommand "SELECT id, faculty_code, code, name FROM course") [] course

    /// One page of courses ordered by faculty and code, with linked channels and the total count.
    member _.Page(faculty: string option, offset: int, limit: int) : Task<(Course * uint64 option) list * int64> =
        task {
            let! rows =
                Sql.query
                    (dataSource.CreateCommand
                        """
                        SELECT c.id, c.faculty_code, c.code, c.name, l.channel_id, count(*) OVER ()
                        FROM course c LEFT JOIN course_channel l ON l.course_id = c.id
                        WHERE $1::text IS NULL OR lower(c.faculty_code) = lower($1)
                        ORDER BY c.faculty_code, c.code
                        LIMIT $2 OFFSET $3
                        """)
                    [ Sql.optional faculty; box limit; box offset ]
                    (fun r -> (course r, Sql.readOptional r 4 (r.GetDecimal >> uint64)), r.GetInt64 5)

            let total = rows |> List.tryHead |> Option.map snd |> Option.defaultValue 0L
            return List.map fst rows, total
        }

    member _.Faculties() : Task<string list> =
        Sql.query (dataSource.CreateCommand "SELECT DISTINCT faculty_code FROM course ORDER BY 1") [] _.GetString(0)

    /// Course ID and channel ID of every linked course.
    member _.Links() : Task<(int64 * uint64) list> =
        Sql.query (dataSource.CreateCommand "SELECT course_id, channel_id FROM course_channel") [] (fun r ->
            r.GetInt64 0, uint64 (r.GetDecimal 1))

    member _.ChannelOf(courseId: int64) : Task<uint64 option> =
        Sql.queryOne
            (dataSource.CreateCommand "SELECT channel_id FROM course_channel WHERE course_id = $1")
            [ box courseId ]
            (fun r -> uint64 (r.GetDecimal 0))

    /// Link a course to a channel, replacing any other link either of them has.
    member _.Link(courseId: int64, channelId: uint64) : Task<unit> =
        task {
            use! connection = dataSource.OpenConnectionAsync()
            use! transaction = connection.BeginTransactionAsync()
            let values = [ box courseId; box (decimal channelId) ]

            do!
                Sql.execute
                    (Sql.command transaction "DELETE FROM course_channel WHERE course_id = $1 OR channel_id = $2")
                    values

            do!
                Sql.execute
                    (Sql.command transaction "INSERT INTO course_channel (course_id, channel_id) VALUES ($1, $2)")
                    values

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
