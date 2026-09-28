return function(root, log)
    local R = {}
    local layers, points, maps = {}, nil, {}
    local engine, image_class, canvas_class, importer, layout
    local textures, by_map, opened, state_text = {}, {}, {}, nil
    local ready, state_revision, last_enabled = false, 0, true
    local events = _G.TreasureNativeEvents
    if not events then
        events = {dirty=true}
        _G.TreasureNativeEvents = events
        NotifyOnNewObject("/Script/DSClient.DLayerMap", function() events.dirty=true end)
        NotifyOnNewObject("/Script/DSClient.DWorldMapData", function() events.dirty=true end)
    end
    events.dirty = true
    local tick, last_error, version, last_pawn = 0, nil, nil, nil
    local function valid(obj)
        local ok, value = pcall(function() return obj ~= nil and obj:IsValid() end)
        return ok and value
    end
    local function name(obj) return valid(obj) and obj:GetFullName() or "" end
    local function load_table(path)
        local f = loadfile(path, "t", {})
        if not f then return nil end
        local ok, value = pcall(f)
        return ok and type(value) == "table" and value or nil
    end
    local function read_version()
        local f = io.open(root.."\\cache\\catalog.version", "r")
        if not f then return nil end
        local value = f:read("*a") f:close() return value
    end
    local function hide(entry)
        for _, item in ipairs(entry.markers) do
            if item.visible and valid(item.widget) then item.widget:SetVisibility(2) item.visible=false end
        end
        if entry.arrow and entry.arrow.visible and valid(entry.arrow.widget) then entry.arrow.widget:SetVisibility(2) entry.arrow.visible=false end
        entry.stamp=nil entry.active=false
    end
    function R.clear()
        for _, entry in pairs(layers) do
            if entry.arrow and valid(entry.arrow.widget) then pcall(function() entry.arrow.widget:RemoveFromParent() end) end
            for _, item in ipairs(entry.markers) do
                if valid(item.widget) then pcall(function() item.widget:RemoveFromParent() end) end
            end
        end
        for _, entry in pairs(layers) do
            if valid(entry.canvas) then pcall(function() entry.canvas:RemoveFromParent() end) end
        end
        layers = {}
        events.dirty = true
    end
    function R.failure(err)
        for _, entry in pairs(layers) do pcall(hide, entry) end
        err = tostring(err)
        if err ~= last_error then log("Renderer: "..err) last_error = err end
    end
    local function save_maps()
        if not version then return end
        local rows = {"return {version="..string.format("%q",version)..",maps={"}
        for id, m in pairs(maps) do
            rows[#rows+1] = string.format("[%d]={size=%.9g,mini=%.9g,dimension=%.9g,cx=%.9g,cy=%.9g},",id,m.size,m.mini,m.dimension,m.cx,m.cy)
        end
        rows[#rows+1] = "}}"
        local f = io.open(root.."\\cache\\maps.lua", "w")
        if f then f:write(table.concat(rows,"\n")) f:close() end
    end
    local function map_id(text)
        text = text:lower()
        local n = text:match("world_0*(%d+)") or text:match("_w(%d+)_")
        return n and tonumber(n)*100 or nil
    end
    local function texture(key, context)
        if not valid(textures[key]) then
            if not valid(importer) then importer=StaticFindObject("/Script/Engine.Default__KismetRenderingLibrary") end
            textures[key]=importer:ImportFileAsTexture2D(context,root.."\\assets\\"..key..".png")
            assert(valid(textures[key]),"Could not import marker texture: "..key)
        end
        return textures[key]
    end
    local function make_marker(parent)
        if not valid(image_class) then image_class=StaticFindObject("/Script/UMG.Image") end
        local widget=StaticConstructObject(image_class,parent)
        assert(valid(widget),"Could not create marker image")
        widget:SetVisibility(2)
        local slot = parent:AddChild(widget)
        slot:SetAutoSize(false)
        slot:SetAnchors({Minimum={X=0,Y=0},Maximum={X=0,Y=0}})
        slot:SetAlignment({X=0.5,Y=0.5})
        slot:SetZOrder(100)
        return {widget=widget,slot=slot}
    end
    local function place(item,x,y,key,size,angle,context)
        if item.key~=key then item.widget:SetBrushFromTexture(texture(key,context),false) item.key=key end
        if item.size~=size then item.slot:SetSize({X=size,Y=size}) item.size=size end
        if item.x~=x or item.y~=y then item.slot:SetPosition({X=x,Y=y}) item.x=x item.y=y end
        if item.angle~=angle then item.widget:SetRenderTransformAngle(angle) item.angle=angle end
        if not item.visible then item.widget:SetVisibility(3) item.visible=true end
    end
    function R.update(enabled)
        tick=tick+1
        if tick==1 or tick%250==0 then
            local current=read_version()
            if current~=version then
                R.clear() points=nil maps={} version=current
                local cached=load_table(root.."\\cache\\maps.lua")
                if cached and cached.version==version and type(cached.maps)=="table" then maps=cached.maps end
            end
        end
        if not points then
            points=load_table(root.."\\cache\\treasures.lua")
            if not points then return end
            by_map={}
            for _,p in ipairs(points) do
                local id=tonumber(tostring(p.section):sub(-3))
                if id then
                    local kind=(p.uid_name or ""):match("^DT_([A-Za-z]+)_G%d+_")
                    kind=kind and kind:lower() or ""
                    p.color=kind=="minigame" and "green" or kind=="map" and "orange" or "white"
                    by_map[id]=by_map[id] or {} table.insert(by_map[id],p)
                end
            end
            log("Catalog indexed: "..#points)
        end
        if tick==1 or tick%25==0 then
            local f=io.open(root.."\\cache\\opened.lua","r")
            local text=f and f:read("*a") or nil
            if f then f:close() end
            if text~=state_text then
                local fn=text and load(text,"opened","t",{})
                local ok,data=pcall(function() return fn and fn() end)
                ready=ok and type(data)=="table" and data.ready and type(data.opened)=="table"
                opened=ready and data.opened or {}
                state_text=text state_revision=state_revision+1
            end
        end
        if not valid(engine) then engine=FindFirstOf("Engine") end
        local ok,pawn=pcall(function() return engine.GameViewport.GameInstance.LocalPlayers[1].PlayerController.Pawn end)
        if not enabled or not ready or not ok or not valid(pawn) then
            if last_enabled then for _,entry in pairs(layers) do pcall(hide,entry) end end
            last_enabled=false return
        end
        last_enabled=true
        local pawn_name=name(pawn)
        if pawn_name~=last_pawn then R.clear() last_pawn=pawn_name end
        -- Construction notifications replace steady global UObject scans.
        if events.dirty or tick==1 or tick%1500==0 then
            events.dirty=false
            local changed=false
            for _,obj in ipairs(FindAllOf("DWorldMapData") or {}) do
                if valid(obj) then
                    local i=obj.WorldMapDataInfo local id=tonumber(i.MapID)
                    local m={size=tonumber(i.WorldMapUISize),mini=tonumber(i.MiniMapUISize),dimension=tonumber(i.MapDimensions),cx=tonumber(i.MapDimensionsCenterX),cy=tonumber(i.MapDimensionsCenterY)}
                    if id and id>0 and m.dimension and m.dimension>0 and m.size>0 and m.mini>0 then
                        local old=maps[id]
                        if not old or old.dimension~=m.dimension or old.size~=m.size or old.mini~=m.mini or old.cx~=m.cx or old.cy~=m.cy then maps[id]=m changed=true end
                    end
                end
            end
            if changed then save_maps() end
            for _,layer in ipairs(FindAllOf("DLayerMap") or {}) do
                local n=name(layer)
                if n:find("/Engine/Transient",1,true) and not layers[n] then
                    layers[n]={layer=layer,markers={},mini=n:find("DLayerMiniMap",1,true)~=nil}
                end
            end
        end
        if not valid(layout) then layout=StaticFindObject("/Script/UMG.Default__WidgetLayoutLibrary") end
        local viewport=layout:GetViewportSize(pawn)
        local dpi=layout:GetViewportScale(pawn)
        local width,height=viewport.X/dpi,viewport.Y/dpi
        local pos=pawn:K2_GetActorLocation()
        for key,entry in pairs(layers) do
            local layer=entry.layer
            if not valid(layer) then layers[key]=nil
            elseif not layer:IsVisible() then if entry.active then hide(entry) end
            else
                local id=entry.mini and map_id(pawn_name) or map_id(name(layer.Map_0_0.Brush.ResourceObject))
                local data=id and maps[id]
                if not data then if entry.active then hide(entry) end
                else
                    local scale=layer.MapOverlay.RenderTransform.Scale.X
                    local origin=layer.MapOverlaySlot:GetPosition()
                    local content=layer.MapOverlayOutSideSlot:GetSize()
                    local stamp=table.concat({id,state_revision,origin.X,origin.Y,content.X,content.Y,scale,width,height,math.floor(pos.X/10),math.floor(pos.Y/10),math.floor(pos.Z/25)},":")
                    if stamp~=entry.stamp then
                        local parent=entry.canvas
                        if not valid(parent) then
                            if not valid(canvas_class) then canvas_class=StaticFindObject("/Script/UMG.CanvasPanel") end
                            parent=StaticConstructObject(canvas_class,layer.MapOverlayOutSide:GetParent())
                            assert(valid(parent),"Could not create marker canvas")
                            parent:SetVisibility(3)
                            local slot=layer.MapOverlayOutSide:GetParent():AddChild(parent)
                            slot:SetAutoSize(false)
                            slot:SetAnchors({Minimum={X=0,Y=0},Maximum={X=0,Y=0}})
                            slot:SetAlignment({X=0,Y=0})
                            slot:SetPosition({X=0,Y=0})
                            slot:SetZOrder(100)
                            entry.canvas=parent entry.canvas_slot=slot
                        end
                        if entry.width~=width or entry.height~=height then
                            entry.canvas_slot:SetSize({X=width,Y=height})
                            entry.width=width entry.height=height
                        end
                        local ratio=(entry.mini and data.mini or data.size)/data.dimension
                        local factor=ratio*scale
                        local offsetX=origin.X+content.X*0.5*scale-data.cx*factor
                        local offsetY=origin.Y+content.Y*0.5*scale-data.cy*factor
                        if entry.offsetX~=offsetX or entry.offsetY~=offsetY then
                            parent:SetRenderTranslation({X=offsetX,Y=offsetY})
                            entry.offsetX=offsetX entry.offsetY=offsetY
                        end
                        local visible,nearest,nearestD={},nil,math.huge
                        for _,p in ipairs(by_map[id] or {}) do
                            if not opened[p.save_id] then
                                local x,y=offsetX+p.x*factor,offsetY+p.y*factor
                                local dx,dy=p.x-pos.X,p.y-pos.Y local d=dx*dx+dy*dy
                                if (entry.mini and d*factor*factor<160*160) or (not entry.mini and x>=-12 and y>=-12 and x<=width+12 and y<=height+12) then
                                    local v={p=p,x=p.x*factor,y=p.y*factor} visible[#visible+1]=v
                                    if d<nearestD then nearest=v nearestD=d end
                                end
                            end
                        end
                        entry.active=true
                        local used,created,complete=0,0,true
                        for _,v in ipairs(visible) do
                            used=used+1
                            local item=entry.markers[used]
                            if not item or not valid(item.widget) then
                                if created>=32 then used=used-1 complete=false break end
                                item=make_marker(parent) entry.markers[used]=item created=created+1
                            end
                            local near=v==nearest
                            place(item,v.x,v.y,v.p.color..(near and "-near" or ""),near and 22 or 16,0,pawn)
                        end
                        for i=used+1,#entry.markers do
                            local item=entry.markers[i]
                            if item.visible and valid(item.widget) then item.widget:SetVisibility(2) item.visible=false end
                        end
                        local dz=nearest and nearest.p.z and nearest.p.z-(pos.Z-100) or 0
                        if nearest and math.abs(dz)>100 then
                            if not entry.arrow or not valid(entry.arrow.widget) then entry.arrow=make_marker(parent) end
                            place(entry.arrow,nearest.x-22,nearest.y,nearest.p.color.."-arrow",40,dz>0 and -90 or 90,pawn)
                        elseif entry.arrow and entry.arrow.visible then entry.arrow.widget:SetVisibility(2) entry.arrow.visible=false end
                        entry.stamp=complete and stamp or nil
                        if used~=entry.last_count and (not entry.last_log or tick-entry.last_log>=100) then entry.last_log=tick log((entry.mini and "Minimap" or "World map").." visible="..used.." pooled="..#entry.markers.." new="..created) entry.last_count=used end
                    end
                end
            end
        end
        last_error=nil
    end
    return R
end
