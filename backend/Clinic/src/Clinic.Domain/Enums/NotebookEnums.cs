namespace Clinic.Domain.Enums;

// Revision kinds are append-only: new kinds may be added, existing values are never reused or
// reassigned. Ink/payload-bearing kinds arrive with the revision-upload phase.
public enum NotebookRevisionKind
{
    Created = 0,
    Payload = 1,
    Amendment = 2,
}
