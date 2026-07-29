local MOD = "[DragonSwordTreasureRadar]"

local function log(message)
    print(string.format("%s %s\n", MOD, tostring(message)))
end

local GENERATION_KEY = "DragonSwordTreasureRadar.Generation"
local previous_generation =
    tonumber(ModRef:GetSharedVariable(GENERATION_KEY)) or 0
local generation = previous_generation + 1
ModRef:SetSharedVariable(GENERATION_KEY, generation)

local function is_current_generation()
    local ok, current_generation = pcall(function()
        return ModRef:GetSharedVariable(GENERATION_KEY)
    end)
    return ok and tonumber(current_generation) == generation
end

local ok_config, config = pcall(require, "config")
if not ok_config then
    log("config.lua could not be loaded: " .. tostring(config))
    return
end

local RADAR_RADIUS = 22500.0
local MAX_RADAR_POINTS = 80
local UPDATE_INTERVAL_MS = 250
local WORLD_MAP_ID = 100

local world_treasures = nil
local enabled = false
local loop_started = false
local update_pending = false
local state_path = nil
local write_error_logged = false
local engine = nil

local function ensure_treasures_loaded()
    if world_treasures ~= nil then
        return true
    end

    local ok_data, data = pcall(require, "treasures")
    if not ok_data or type(data) ~= "table" then
        log("treasures.lua could not be loaded: " .. tostring(data))
        return false
    end

    world_treasures = {}
    for _, treasure in ipairs(data) do
        local map_id = tonumber(string.sub(tostring(treasure.section), -3))
        local x = tonumber(treasure.x)
        local y = tonumber(treasure.y)
        local save_id = tonumber(treasure.save_id)
        if map_id == WORLD_MAP_ID
            and x ~= nil
            and y ~= nil
            and save_id ~= nil
        then
            table.insert(world_treasures, {
                save_id = save_id,
                x = x,
                y = y,
            })
        end
    end

    log(string.format(
        "Loaded %d exact-CID world treasure locations.",
        #world_treasures
    ))
    return true
end

local function resolve_state_path()
    if state_path ~= nil then
        return state_path
    end

    local source = debug.getinfo(1, "S").source
    if type(source) == "string" and string.sub(source, 1, 1) == "@" then
        local script_file = string.sub(source, 2)
        local scripts_directory = string.match(
            script_file,
            "^(.*)[/\\][^/\\]+$"
        )
        local mod_directory = scripts_directory
            and string.match(scripts_directory, "^(.*)[/\\][^/\\]+$")
        if mod_directory ~= nil then
            state_path = mod_directory .. "\\radar_state.json"
        end
    end

    if state_path == nil then
        local ok, directories = pcall(IterateGameDirectories)
        local win64 = ok and directories
            and directories.Game
            and directories.Game.Binaries
            and directories.Game.Binaries.Win64
        local win64_path = win64 and win64.__absolute_path
        if type(win64_path) == "string" then
            state_path = win64_path
                .. "\\Mods\\DragonSwordTreasureMap\\radar_state.json"
        end
    end

    if state_path ~= nil then
        log("Radar bridge file: " .. state_path)
    end
    return state_path
end

local function write_text_atomic(path, text)
    local temporary_path = path .. ".tmp"
    local file, open_error = io.open(temporary_path, "w")
    if file == nil then
        return false, open_error
    end

    file:write(text)
    file:close()
    os.remove(path)
    local renamed, rename_error = os.rename(temporary_path, path)
    if not renamed then
        return false, rename_error
    end
    return true, nil
end

local function write_disabled_state()
    local path = resolve_state_path()
    if path ~= nil then
        write_text_atomic(path, '{"enabled":false,"points":[]}')
    end
end

local function get_player_location()
    local ok, player_x, player_y = pcall(function()
        if engine == nil then
            engine = FindFirstOf("Engine")
        end
        if engine == nil then
            return nil, nil
        end

        local viewport = engine.GameViewport
        if viewport == nil then
            return nil, nil
        end

        local game_instance = viewport.GameInstance
        if game_instance == nil then
            return nil, nil
        end

        local local_players = game_instance.LocalPlayers
        if local_players == nil then
            return nil, nil
        end

        local local_player = local_players[1]
        if local_player == nil then
            return nil, nil
        end

        local controller = local_player.PlayerController
        if controller == nil then
            return nil, nil
        end

        local pawn = controller.Pawn
        if pawn == nil then
            return nil, nil
        end

        local location = pawn:K2_GetActorLocation()
        if location == nil then
            return nil, nil
        end

        return tonumber(location.X), tonumber(location.Y)
    end)
    if not ok then
        engine = nil
        return nil, nil
    end
    return player_x, player_y
end

local function build_radar_json(player_x, player_y)
    local radius_squared = RADAR_RADIUS * RADAR_RADIUS
    local nearby = {}

    for _, treasure in ipairs(world_treasures) do
        local delta_x = treasure.x - player_x
        local delta_y = treasure.y - player_y
        local distance_squared = delta_x * delta_x + delta_y * delta_y
        if distance_squared <= radius_squared then
            table.insert(nearby, {
                save_id = treasure.save_id,
                dx = delta_x,
                dy = delta_y,
                distance_squared = distance_squared,
            })
        end
    end

    table.sort(nearby, function(left, right)
        return left.distance_squared < right.distance_squared
    end)

    local count = math.min(#nearby, MAX_RADAR_POINTS)
    local parts = {
        string.format(
            '{"enabled":true,"radius":%.3f,"points":[',
            RADAR_RADIUS
        ),
    }
    for index = 1, count do
        local point = nearby[index]
        if index > 1 then
            table.insert(parts, ",")
        end
        table.insert(parts, string.format(
            '{"saveId":%d,"dx":%.3f,"dy":%.3f}',
            point.save_id,
            point.dx,
            point.dy
        ))
    end
    table.insert(parts, "]}")
    return table.concat(parts)
end

local function update_radar_state()
    if not enabled or not ensure_treasures_loaded() then
        return
    end

    local player_x, player_y = get_player_location()
    if player_x == nil or player_y == nil then
        return
    end

    local path = resolve_state_path()
    if path == nil then
        return
    end

    local written, write_error = write_text_atomic(
        path,
        build_radar_json(player_x, player_y)
    )
    if not written then
        if not write_error_logged then
            log("Could not write radar state: " .. tostring(write_error))
            write_error_logged = true
        end
    elseif write_error_logged then
        log("Radar state output recovered")
        write_error_logged = false
    end
end

local function queue_radar_update()
    if not is_current_generation() or not enabled or update_pending then
        return
    end

    update_pending = true
    ExecuteInGameThread(function()
        if is_current_generation() then
            pcall(update_radar_state)
        end
        update_pending = false
    end)
end

local function ensure_loop_started()
    if loop_started then
        return
    end
    loop_started = true

    LoopAsync(UPDATE_INTERVAL_MS, function()
        if not is_current_generation() then
            return true
        end
        queue_radar_update()
        return false
    end)
end

RegisterKeyBind(Key[config.refresh_key], function()
    if not is_current_generation() then
        return
    end
    if ensure_treasures_loaded() then
        enabled = true
        ensure_loop_started()
        log("External radar enabled.")
        queue_radar_update()
    end
end)

RegisterKeyBind(Key[config.toggle_key], function()
    if not is_current_generation() then
        return
    end
    enabled = not enabled
    if enabled and ensure_treasures_loaded() then
        ensure_loop_started()
        queue_radar_update()
    else
        write_disabled_state()
    end
    log("External radar " .. (enabled and "enabled" or "disabled"))
end)

write_disabled_state()
log("Ready. Press F7 to start and F8 to toggle.")
