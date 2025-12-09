# TWAIN DSM Dependencies

This directory contains the TWAIN Data Source Manager (DSM) DLLs required for scanner functionality.

## Required Files

For building the installer, download the TWAIN DSM from:
https://github.com/twain/twain-dsm/releases

### For 64-bit installer (ScanWithWeb_x64.iss):
- `twaindsm_x64.dll` - 64-bit TWAIN DSM

### For 32-bit installer (ScanWithWeb_x86.iss):
- `twaindsm_x86.dll` - 32-bit TWAIN DSM

## Download Instructions

1. Go to https://github.com/twain/twain-dsm/releases
2. Download the latest release (e.g., `TWAINDSM_2.5.1.msi`)
3. Extract the DLLs from the MSI:
   - 64-bit: Extract `TWAINDSM.dll` and rename to `twaindsm_x64.dll`
   - 32-bit: Extract the 32-bit version and rename to `twaindsm_x86.dll`

## GitHub Actions

The GitHub Actions workflow automatically downloads these DLLs during the build process.
No manual download is required for CI/CD builds.

## Note

These DLLs are from the official TWAIN Working Group and are redistributable.
License: LGPL v2.1 (https://github.com/twain/twain-dsm/blob/master/COPYING.LESSER)
