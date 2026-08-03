local WorldMap = {}
local cached_layer = nil
local cached_data = {}
local known_layers = {}

-- During map loading, all cached UObjects are considered unsafe.
local suspended = false
local needs_rescan = true

local function is_valid(object)
    if object == nil then
        return false
    end

    local ok, valid = pcall(function()
        return object:IsValid()
    end)

    return ok and valid == true
end

local function safe_get(object, name)
    if object == nil then
        return nil
    end

    local ok, value = pcall(function()
        return object[name]
    end)

    return ok and value or nil
end

local function safe_call(object, name, ...)
    if object == nil then
        return false, nil
    end

    local arguments = { ... }

    return pcall(function()
        local callable = object[name]
        if callable == nil then
            error("function not found: " .. tostring(name))
        end

        return callable(object, table.unpack(arguments))
    end)
end

local function full_name(object)
    if object == nil then
        return "<nil>"
    end

    local ok, name = pcall(function()
        return object:GetFullName()
    end)

    return ok and tostring(name) or "<unavailable>"
end

local function is_visible(object)
    if not is_valid(object) then
        return false
    end

    local ok, visible = safe_call(object, "IsVisible")
    return ok and visible == true
end

local function is_world_map_layer(layer)
    if not is_valid(layer) then
        return false
    end

    local name = full_name(layer)

    return string.find(name, "/Engine/Transient", 1, true) ~= nil
        and string.find(name, ".DPanelWorldMap_C.", 1, true) ~= nil
end

local function remember_world_map_layer(layer)
    if suspended then
        return
    end

    if not is_valid(layer) or not is_world_map_layer(layer) then
        return
    end

    -- Remove invalid entries while checking for duplicates.
    for index = #known_layers, 1, -1 do
        local known = known_layers[index]

        if not is_valid(known) then
            table.remove(known_layers, index)
        elseif known == layer then
            return
        end
    end

    table.insert(known_layers, layer)
end

local function scan_world_map_layers()
    if suspended then
        return
    end

    local scan_ok, layers = pcall(function()
        return FindAllOf("DLayerMap_C")
    end)

    if scan_ok and layers ~= nil then
        for _, layer in ipairs(layers) do
            remember_world_map_layer(layer)
        end
    end

    needs_rescan = false
end

local function find_world_map_layer()
    if suspended then
        return nil
    end

    if needs_rescan then
        scan_world_map_layers()
    end

    if is_visible(cached_layer) then
        return cached_layer
    end

    cached_layer = nil

    for index = #known_layers, 1, -1 do
        local layer = known_layers[index]

        if not is_valid(layer) then
            table.remove(known_layers, index)
        elseif is_visible(layer) then
            cached_layer = layer
            return layer
        end
    end

    return nil
end

local function detect_map_id(layer)
    local image = safe_get(layer, "Map_0_0")
    local brush = safe_get(image, "Brush")
    local resource = safe_get(brush, "ResourceObject")
    local name = full_name(resource)

    if string.find(name, "_W2_", 1, true)
        or string.find(name, "world_02", 1, true)
    then
        return 200
    end

    if string.find(name, "_W1_", 1, true)
        or string.find(name, "world_01", 1, true)
    then
        return 100
    end

    return nil
end

local function get_map_data(map_id)
    if map_id == nil then
        return nil
    end

    if cached_data[map_id] ~= nil then
        return cached_data[map_id]
    end

    local ok, objects = pcall(function()
        return FindAllOf("DWorldMapData")
    end)

    if not ok or objects == nil then
        return nil
    end

    for _, object in ipairs(objects) do
        local info = safe_get(object, "WorldMapDataInfo")

        local values_ok, data = pcall(function()
            if info == nil or tonumber(info.MapID) ~= map_id then
                return nil
            end

            return {
                map_id = map_id,
                dimensions = tonumber(info.MapDimensions),
                ui_size = tonumber(info.WorldMapUISize),
            }
        end)

        if values_ok
            and data ~= nil
            and data.dimensions ~= nil
            and data.dimensions > 0.0
            and data.ui_size ~= nil
            and data.ui_size > 0.0
        then
            cached_data[map_id] = data
            return data
        end
    end

    return nil
end

function WorldMap.reset()
    -- Drop every UObject wrapper retained from the previous world.
    cached_layer = nil
    cached_data = {}
    known_layers = {}
    needs_rescan = true
end

function WorldMap.set_suspended(value)
    local new_value = value == true

    if suspended == new_value then
        if not new_value then
            needs_rescan = true
        end
        return
    end

    suspended = new_value

    if suspended then
        WorldMap.reset()
    else
        -- The new world has finished loading. Search for newly created widgets.
        needs_rescan = true
    end
end

function WorldMap.initialize(log, is_current_generation)
    scan_world_map_layers()

    local notify_ok, notify_error = pcall(function()
        NotifyOnNewObject(
            "/Script/DSClient.DLayerMap",
            function(created_object)
                if is_current_generation() and not suspended then
                    remember_world_map_layer(created_object)
                end
            end
        )
    end)

    if not notify_ok then
        log(
            "World-map creation observer failed: "
                .. tostring(notify_error)
        )
    end
end

function WorldMap.read_state()
    if suspended then
        return nil
    end

    local layer = find_world_map_layer()
    if not is_valid(layer) then
        return nil
    end

    local visible_ok, visible = safe_call(layer, "IsVisible")
    if visible_ok and visible == false then
        cached_layer = nil
        return nil
    end

    local map_id = detect_map_id(layer)
    local data = get_map_data(map_id)

    if data == nil then
        return nil
    end

    local overlay = safe_get(layer, "MapOverlay")
    local slot = safe_get(layer, "MapOverlaySlot")

    if overlay == nil or slot == nil then
        return nil
    end

    local layout_ok, layout = pcall(
        StaticFindObject,
        "/Script/UMG.Default__WidgetLayoutLibrary"
    )

    if not layout_ok or layout == nil then
        return nil
    end

    local position_ok, position = safe_call(slot, "GetPosition")
    local viewport_ok, viewport_size = safe_call(
        layout,
        "GetViewportSize",
        layer
    )
    local viewport_scale_ok, viewport_scale = safe_call(
        layout,
        "GetViewportScale",
        layer
    )

    local transform = safe_get(overlay, "RenderTransform")
    local scale = safe_get(transform, "Scale")
    local player = safe_get(layer, "PlayerIconWidget")
    local player_transform = safe_get(player, "RenderTransform")
    local player_translation = safe_get(
        player_transform,
        "Translation"
    )

    if not position_ok
        or not viewport_ok
        or not viewport_scale_ok
        or position == nil
        or viewport_size == nil
        or viewport_scale == nil
        or scale == nil
        or player_translation == nil
    then
        return nil
    end

    local values_ok, state = pcall(function()
        return {
            map_id = data.map_id,
            dimensions = data.dimensions,
            ui_size = data.ui_size,
            left = tonumber(position.X),
            top = tonumber(position.Y),
            zoom = tonumber(scale.X),
            viewport_width = tonumber(viewport_size.X),
            viewport_height = tonumber(viewport_size.Y),
            viewport_scale = tonumber(viewport_scale),
            player_map_x = data.ui_size * 0.5
                + tonumber(player_translation.X),
            player_map_y = data.ui_size * 0.5
                + tonumber(player_translation.Y),
        }
    end)

    if not values_ok
        or state == nil
        or state.left == nil
        or state.top == nil
        or state.zoom == nil
        or state.zoom <= 0.0
        or state.viewport_width == nil
        or state.viewport_width <= 0.0
        or state.viewport_height == nil
        or state.viewport_height <= 0.0
        or state.viewport_scale == nil
        or state.viewport_scale <= 0.0
        or state.player_map_x == nil
        or state.player_map_y == nil
    then
        return nil
    end

    return state
end

return WorldMap
