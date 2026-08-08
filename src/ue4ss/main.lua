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

local ok_world_map, world_map = pcall(require, "world_map")
if not ok_world_map then
    log("world_map.lua could not be loaded: " .. tostring(world_map))
    return
end
local world_map_markers_enabled = config.world_map_markers ~= false
local show_height = config.show_height ~= false
local show_treasure_types = config.show_treasure_types ~= false
local text_scale = tonumber(config.text_scale) or 1.0
text_scale = math.max(0.5, math.min(2.0, text_scale))
if world_map_markers_enabled then
    world_map.initialize(log, is_current_generation)
    -- Avoid LoadMap hooks: this game is more stable when world changes are
    -- detected through temporary Pawn loss and delayed widget rescanning.
    log("World-map safety mode: Pawn-loss detection enabled.")
end

local TOWN_RADAR_RADIUS = 12500.0
local FIELD_RADAR_RADIUS = 22500.0
local MINIMAP_SCALE_THRESHOLD = 2.7
local MINIMAP_SCALE_CHECK_INTERVAL_MS = 1000
local MAX_RADAR_POINTS = 80
-- Keep timing internal while preserving responsive world-map tracking.
local WORLD_MAP_UPDATE_INTERVAL_MS = 32
local MAP_LOAD_RESUME_DELAY_MS = 3000

local MINIMAP_UPDATE_INTERVAL_MS = 250
local WORLD_MAP_ID = 100
local TREASURE_GRID_CELL_SIZE = FIELD_RADAR_RADIUS

local world_treasures = nil
local treasure_grid = nil
local radar_radius = FIELD_RADAR_RADIUS
local enabled = false
local loop_started = false
local world_map_loop_started = false
local update_pending = false
local state_path = nil
local write_error_logged = false
local engine = nil
local minimap_layer = nil
local minimap_scale_elapsed_ms = MINIMAP_SCALE_CHECK_INTERVAL_MS
local minimap_mode = nil
local world_map_was_active = false
local map_resume_delay_remaining_ms = 0

local function ensure_treasures_loaded()
    if world_treasures ~= nil then
        return true
    end

    local ok_data, data = pcall(require, "treasures")
    if not ok_data or type(data) ~= "table" then
        log("treasures.lua could not be loaded: " .. tostring(data))
        return false
    end

    -- PosZ is generated locally and forwarded to the overlay for height
    -- diagnostics. UIDName remains in treasures.lua for overlay-side labels.
    world_treasures = {}
    treasure_grid = {}
    for _, treasure in ipairs(data) do
        local map_id = tonumber(string.sub(tostring(treasure.section), -3))
        local x = tonumber(treasure.x)
        local y = tonumber(treasure.y)
        local z = tonumber(treasure.z)
        local save_id = tonumber(treasure.save_id)
        if map_id == WORLD_MAP_ID
            and x ~= nil
            and y ~= nil
            and save_id ~= nil
        then
            local point = {
                save_id = save_id,
                x = x,
                y = y,
                z = z,
                has_z = z ~= nil,
            }
            table.insert(world_treasures, point)

            local cell_x = math.floor(x / TREASURE_GRID_CELL_SIZE)
            local cell_y = math.floor(y / TREASURE_GRID_CELL_SIZE)
            local cell_key = tostring(cell_x) .. ":" .. tostring(cell_y)
            local cell = treasure_grid[cell_key]
            if cell == nil then
                cell = {}
                treasure_grid[cell_key] = cell
            end
            table.insert(cell, point)
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
    local temporary_path = path
        .. "."
        .. tostring(generation)
        .. ".tmp"
    local file, open_error = io.open(temporary_path, "w")
    if file == nil then
        return false, open_error
    end

    local write_ok, write_result, write_error = pcall(
        file.write,
        file,
        text
    )
    if not write_ok or write_result == nil then
        pcall(file.close, file)
        os.remove(temporary_path)
        return false, write_ok and write_error or write_result
    end
    local close_ok, close_result, close_error = pcall(
        file.close,
        file
    )
    if not close_ok or close_result == nil then
        os.remove(temporary_path)
        return false, close_ok and close_error or close_result
    end
    os.remove(path)
    local renamed, rename_error = os.rename(temporary_path, path)
    if not renamed then
        os.remove(temporary_path)
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
    local ok, player_x, player_y, player_z = pcall(function()
        if engine == nil then
            engine = FindFirstOf("Engine")
        end
        if engine == nil then
            return nil, nil, nil
        end

        local viewport = engine.GameViewport
        if viewport == nil then
            return nil, nil, nil
        end

        local game_instance = viewport.GameInstance
        if game_instance == nil then
            return nil, nil, nil
        end

        local local_players = game_instance.LocalPlayers
        if local_players == nil then
            return nil, nil, nil
        end

        local local_player = local_players[1]
        if local_player == nil then
            return nil, nil, nil
        end

        local controller = local_player.PlayerController
        if controller == nil then
            return nil, nil, nil
        end

        local pawn = controller.Pawn
        if pawn == nil then
            return nil, nil, nil
        end

        local location = pawn:K2_GetActorLocation()
        if location == nil then
            return nil, nil, nil
        end

        return tonumber(location.X),
            tonumber(location.Y),
            tonumber(location.Z)
    end)
    if not ok then
        engine = nil
        return nil, nil, nil
    end
    return player_x, player_y, player_z
end

local function is_valid_object(object)
    if object == nil then
        return false
    end
    local ok, valid = pcall(function()
        return object:IsValid()
    end)
    return ok and valid == true
end

local function read_minimap_scale()
    if not is_valid_object(minimap_layer) then
        minimap_layer = FindFirstOf("DLayerMiniMap")
    end
    if not is_valid_object(minimap_layer) then
        minimap_layer = nil
        return nil
    end

    local ok, scale = pcall(function()
        local layer_map = minimap_layer.LayerMap
        if not is_valid_object(layer_map) then
            return nil
        end

        local map_overlay = layer_map.MapOverlay
        if not is_valid_object(map_overlay) then
            return nil
        end

        return tonumber(map_overlay.RenderTransform.Scale.X)
    end)
    if not ok or scale == nil then
        minimap_layer = nil
        return nil
    end
    return scale
end

local function update_radar_radius()
    local scale = read_minimap_scale()
    if scale == nil then
        return
    end

    local new_mode
    local new_radius
    if scale >= MINIMAP_SCALE_THRESHOLD then
        new_mode = "town"
        new_radius = TOWN_RADAR_RADIUS
    else
        new_mode = "field"
        new_radius = FIELD_RADAR_RADIUS
    end

    radar_radius = new_radius
    if minimap_mode ~= new_mode then
        minimap_mode = new_mode
        log(string.format(
            "Minimap scale %.3f detected; using %.0f m radar range.",
            scale,
            new_radius / 100.0
        ))
    end
end

local function build_radar_json(player_x, player_y, player_z)
    local radius_squared = radar_radius * radar_radius
    local nearby = {}
    local center_cell_x =
        math.floor(player_x / TREASURE_GRID_CELL_SIZE)
    local center_cell_y =
        math.floor(player_y / TREASURE_GRID_CELL_SIZE)
    local cell_range =
        math.ceil(radar_radius / TREASURE_GRID_CELL_SIZE)

    for offset_x = -cell_range, cell_range do
        for offset_y = -cell_range, cell_range do
            local cell_key =
                tostring(center_cell_x + offset_x)
                .. ":"
                .. tostring(center_cell_y + offset_y)
            local cell = treasure_grid[cell_key]
            if cell ~= nil then
                for _, treasure in ipairs(cell) do
                    local delta_x = treasure.x - player_x
                    local delta_y = treasure.y - player_y
                    local distance_squared =
                        delta_x * delta_x + delta_y * delta_y
                    if distance_squared <= radius_squared then
                        table.insert(nearby, {
                            save_id = treasure.save_id,
                            x = treasure.x,
                            y = treasure.y,
                            z = treasure.z or 0,
                            has_z = treasure.has_z == true,
                            dx = delta_x,
                            dy = delta_y,
                            distance_squared = distance_squared,
                        })
                    end
                end
            end
        end
    end

    table.sort(nearby, function(left, right)
        return left.distance_squared < right.distance_squared
    end)

    local count = math.min(#nearby, MAX_RADAR_POINTS)
    local parts = {
        string.format(
            '{"enabled":true,"showHeight":%s,"showTreasureTypes":%s,"textScale":%.3f,"playerZ":%.3f,"hasPlayerZ":%s,"radius":%.3f,"points":[',
            show_height and "true" or "false",
            show_treasure_types and "true" or "false",
            text_scale,
            player_z or 0,
            player_z ~= nil and "true" or "false",
            radar_radius
        ),
    }
    for index = 1, count do
        local point = nearby[index]
        if index > 1 then
            table.insert(parts, ",")
        end
        table.insert(parts, string.format(
            '{"saveId":%d,"x":%.3f,"y":%.3f,"z":%.3f,"hasZ":%s,"dx":%.3f,"dy":%.3f}',
            point.save_id,
            point.x,
            point.y,
            point.z,
            point.has_z and "true" or "false",
            point.dx,
            point.dy
        ))
    end
    table.insert(parts, "]}")
    return table.concat(parts)
end

local function build_world_map_json(map, player_x, player_y, player_z)
    return string.format(
        '{"enabled":true,"showHeight":%s,"showTreasureTypes":%s,"textScale":%.3f,"playerZ":%.3f,"hasPlayerZ":%s,"mode":"world","worldMap":'
            .. '{"mapId":%d,"dimensions":%.3f,'
            .. '"uiSize":%.3f,"left":%.3f,"top":%.3f,'
            .. '"zoom":%.6f,"viewportWidth":%.3f,'
            .. '"viewportHeight":%.3f,"viewportScale":%.6f,'
            .. '"hasViewportTransform":%s,'
            .. '"viewportOriginX":%.9f,"viewportOriginY":%.9f,'
            .. '"viewportAxisX":%.9f,"viewportAxisY":%.9f,'
            .. '"playerWorldX":%.3f,"playerWorldY":%.3f,'
            .. '"playerMapX":%.3f,"playerMapY":%.3f},'
            .. '"points":[]}',
        show_height and "true" or "false",
        show_treasure_types and "true" or "false",
        text_scale,
        player_z or 0,
        player_z ~= nil and "true" or "false",
        map.map_id,
        map.dimensions,
        map.ui_size,
        map.left,
        map.top,
        map.zoom,
        map.viewport_width,
        map.viewport_height,
        map.viewport_scale,
        map.has_viewport_transform and "true" or "false",
        map.viewport_origin_x,
        map.viewport_origin_y,
        map.viewport_axis_x,
        map.viewport_axis_y,
        player_x,
        player_y,
        map.player_map_x,
        map.player_map_y
    )
end

local function update_radar_state()
    if not enabled or not ensure_treasures_loaded() then
        return
    end

    local player_x, player_y, player_z = get_player_location()
    if player_x == nil or player_y == nil then
        engine = nil
        minimap_layer = nil
        minimap_mode = nil
        world_map_was_active = false

        if world_map_markers_enabled then
            world_map.set_suspended(true)
            map_resume_delay_remaining_ms =
                MAP_LOAD_RESUME_DELAY_MS
        end

        write_disabled_state()
        return
    end

    local map_state = nil
    if world_map_markers_enabled then
        map_state = world_map.read_state()
    end

    local output
    if map_state ~= nil then
        world_map_was_active = true
        output = build_world_map_json(
            map_state,
            player_x,
            player_y,
            player_z
        )
    else
        world_map_was_active = false

        minimap_scale_elapsed_ms =
            minimap_scale_elapsed_ms
                + MINIMAP_UPDATE_INTERVAL_MS

        if minimap_scale_elapsed_ms
            >= MINIMAP_SCALE_CHECK_INTERVAL_MS
        then
            minimap_scale_elapsed_ms = 0
            update_radar_radius()
        end

        output = build_radar_json(
            player_x,
            player_y,
            player_z
        )
    end

    local path = resolve_state_path()
    if path == nil then
        return
    end

    local written, write_error = write_text_atomic(
        path,
        output
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

local ensure_world_map_loop_started

local function queue_radar_update()
    if not is_current_generation()
        or not enabled
        or update_pending
        or map_resume_delay_remaining_ms > 0
    then
        return
    end

    update_pending = true
    ExecuteInGameThread(function()
        if is_current_generation()
            and map_resume_delay_remaining_ms <= 0
        then
            pcall(update_radar_state)
        end
        update_pending = false
        if world_map_was_active then
            ensure_world_map_loop_started()
        end
    end)
end

ensure_world_map_loop_started = function()
    if world_map_loop_started then
        return
    end
    world_map_loop_started = true

    LoopAsync(WORLD_MAP_UPDATE_INTERVAL_MS, function()
        if not is_current_generation() then
            return true
        end
        if not enabled or not world_map_was_active then
            world_map_loop_started = false
            return true
        end
        queue_radar_update()
        return false
    end)
end

local function ensure_loop_started()
    if loop_started then
        return
    end
    loop_started = true

    LoopAsync(MINIMAP_UPDATE_INTERVAL_MS, function()
        if not is_current_generation() then
            return true
        end

        if map_resume_delay_remaining_ms > 0 then
            map_resume_delay_remaining_ms = math.max(
                0,
                map_resume_delay_remaining_ms
                    - MINIMAP_UPDATE_INTERVAL_MS
            )

            if map_resume_delay_remaining_ms == 0
                and world_map_markers_enabled
            then
                world_map.set_suspended(false)
            end

            return false
        end

        queue_radar_update()
        if world_map_was_active then
            ensure_world_map_loop_started()
        end
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
