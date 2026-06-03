using WpfApp3.Utilities;

namespace WpfApp3.Services
{
    /// <summary>
    /// Service per gestione Power Plans e CPU tuning
    /// </summary>
    public class PowerService
    {
        /// <summary>
        /// Ottiene il power plan attivo
        /// </summary>
        public async Task<string> GetActivePowerPlanAsync()
        {
            var result = await Task.Run(() =>
            {
                var cmd = CommandHelper.RunCmdFull("powercfg", "/getactivescheme");
                return cmd.Output?.Trim() ?? "?";
            });

            return result;
        }

        /// <summary>
        /// Imposta un power plan
        /// </summary>
        public async Task<bool> SetPowerPlanAsync(string planName)
        {
            if (!AppConstants.PowerPlans.TryGetValue(planName, out var guid))
                return false;

            var result = await Task.Run(() =>
            {
                var cmd = CommandHelper.RunCmdFull("powercfg", $"/setactive {guid}");
                return cmd.ExitCode == 0;
            });

            return result;
        }

        /// <summary>
        /// Abilita/disabilita il Core Parking
        /// </summary>
        public async Task<string> SetCoreParking(bool enabled)
        {
            var value = enabled ? "0" : "1";
            var cmd = $@"Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3747d7f1ac' -Name 'Attributes' | Set-ItemProperty -Name 'Attributes' -Value '{value}'";

            var result = await Task.Run(() =>
            {
                return CommandHelper.RunPsFull(cmd);
            });

            return result.ExitCode == 0 ? "OK" : $"FAILED: {result.Error}";
        }

        /// <summary>
        /// Abilita/disabilita HAGS (Hardware Accelerated GPU Scheduling)
        /// </summary>
        public async Task<string> SetHags(bool enabled)
        {
            var value = enabled ? "1" : "0";
            var cmd = $@"reg add 'HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' /v HwSchMode /t REG_DWORD /d {value} /f";

            var result = await Task.Run(() =>
            {
                return CommandHelper.RunCmdFull("powershell", $"-NoProfile -Command \"{cmd}\"");
            });

            return result.ExitCode == 0 ? "OK" : $"FAILED: {result.Error}";
        }

        /// <summary>
        /// Configura la velocità del mouse (accelerazione on/off)
        /// </summary>
        public async Task<string> SetMouseAcceleration(bool enabled)
        {
            var pairs = enabled
                ? AppConstants.MouseAccelOnPairs
                : AppConstants.MouseAccelOffPairs;

            var results = await Task.Run(() =>
            {
                var details = new List<string>();

                foreach (var (key, value) in pairs)
                {
                    var cmd = $@"reg add 'HKCU\Control Panel\Mouse' /v {key} /t REG_SZ /d {value} /f";
                    var result = CommandHelper.RunCmdFull("powershell", $"-NoProfile -Command \"{cmd}\"");
                    details.Add($"{key} = {value} -> {result.ExitCode}");
                }

                return details;
            });

            return string.Join("\n", results);
        }

        /// <summary>
        /// Abilita/disabilita il touchpad
        /// </summary>
        public async Task<string> SetTouchpad(bool enabled)
        {
            var value = enabled ? "1" : "0";
            var cmd = $@"reg add 'HKCU\Software\Synaptics\SynTPEnh' /v TouchpadOnOff /t REG_DWORD /d {value} /f";

            var result = await Task.Run(() =>
            {
                return CommandHelper.RunCmdFull("powershell", $"-NoProfile -Command \"{cmd}\"");
            });

            return result.ExitCode == 0 ? "OK" : $"FAILED: {result.Error}";
        }
    }
}
