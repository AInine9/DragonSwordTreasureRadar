local source = debug.getinfo(1, "S").source:sub(2)
local root = source:match("^(.*)[/\\]scripts[/\\][^/\\]+$")
assert(root, "Could not locate native radar directory")
local function log(s) print("[TreasureNative] " .. tostring(s) .. "\n") end
local load_native, err = package.loadlib(root .. "\\radar_native.dll", "luaopen_radar_native")
if not load_native then log(err) return end
load_native()
local renderer = dofile(root .. "\\scripts\\renderer.lua")(root, log)
local enabled = true
RegisterKeyBind(Key.F8, function()
    enabled = not enabled
    log(enabled and "Enabled" or "Hidden")
end)
local pending = false
LoopAsync(50, function()
    if pending then return end
    pending = true
    ExecuteInGameThread(function()
        local ok, error = pcall(renderer.update, enabled)
        if not ok then renderer.failure(error) end
        pending = false
    end)
end)
log("Treasure Radar v2.0.0 loaded. F8 toggles markers.")
