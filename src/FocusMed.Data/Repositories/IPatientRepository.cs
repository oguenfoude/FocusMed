using FocusMed.Data.Entities;

namespace FocusMed.Data.Repositories;

public interface IPatientRepository
{
    Task<Patient?> GetByIdAsync(int id);
    Task<Patient?> GetByPatientIdAsync(string patientId);
    Task AddAsync(Patient patient);
    Task UpdateAsync(Patient patient);
}
