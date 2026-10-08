fx_version 'cerulean'
games { 'gta5enhanced' }

name 'vMenu.Clothing'
description 'vMenu plugin that adds a thermal based clothing system'
version '1.0.0'
author 'RickyB505'

files {
    'client/*.dll',
    'client/CitizenFX.FiveM.Shared.dll',
    'client/CitizenFX.FiveM.Client.dll',
}

server_script 'server/vMenu.Clothing.Server.dll'
client_script 'client/vMenu.Clothing.Client.dll'
