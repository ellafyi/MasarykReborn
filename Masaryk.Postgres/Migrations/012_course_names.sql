-- Courses keep only their current name; nothing read the per-term history.
ALTER TABLE course ADD COLUMN name text;

UPDATE course c
SET name = latest.name
FROM (SELECT DISTINCT ON (o.course_id) o.course_id, r.name
      FROM course_offering o
               JOIN course_revision r ON r.offering_id = o.id AND r.present_in_export
      ORDER BY o.course_id, r.catalog_import_id DESC) latest
WHERE latest.course_id = c.id;

DELETE FROM course WHERE name IS NULL AND id NOT IN (SELECT course_id FROM course_channel);

ALTER TABLE course
    ALTER COLUMN name SET NOT NULL,
    DROP CONSTRAINT course_faculty_code_fkey;

DROP TABLE course_revision, course_offering, catalog_import, faculty;
