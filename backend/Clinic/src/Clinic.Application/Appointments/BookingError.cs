namespace Clinic.Application.Appointments;

public enum BookingError
{
    None = 0,
    InvalidPatientId = 1,
    InvalidTimeRange = 2,
    StartMustBeInFuture = 3,
    PatientNotFound = 4,
    TimeSlotUnavailable = 5,
        InvalidDoctorId = 6,
    DoctorNotFound = 7,
    DoctorInactive = 8
}
