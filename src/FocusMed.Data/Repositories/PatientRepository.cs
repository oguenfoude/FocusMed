using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Data.Repositories;

public class PatientRepository : IPatientRepository
{
    private readonly FocusMedDbContext _context;

    public PatientRepository(FocusMedDbContext context)
    {
        _context = context;
    }

    public async Task<Patient?> GetByIdAsync(int id)
    {
        return await _context.Patients.FindAsync(id);
    }

    public async Task<Patient?> GetByPatientIdAsync(string patientId)
    {
        return await _context.Patients.FirstOrDefaultAsync(p => p.PatientId == patientId);
    }

    public async Task AddAsync(Patient patient)
    {
        await _context.Patients.AddAsync(patient);
    }

    public Task UpdateAsync(Patient patient)
    {
        _context.Patients.Update(patient);
        return Task.CompletedTask;
    }
}
