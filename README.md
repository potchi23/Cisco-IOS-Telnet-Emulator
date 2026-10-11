# SwitchTelnetEmulator

Emulador educativo de la CLI de un switch Cisco-like de capa 2. Permite practicar comandos IOS habituales mediante Telnet. **No es IOS/Cisco Packet Tracer**, no reenvía tramas Ethernet y no realiza convergencia STP ni intercambia BPDUs.

## Requisitos

- .NET SDK 9 o posterior (el proyecto apunta a `net9.0`; en el entorno original se comprobó con SDK 9.0.102).

## Iniciar

### Ejecutable para doble clic (Windows x64)

Haz doble clic en `publicar-windows.bat`. Publicará una versión autónoma en `publish\win-x64\SwitchTelnetEmulator.exe`; no requiere instalar .NET en el equipo donde se ejecute. Deja `emulator-settings.json` junto al `.exe` y ábrelo con el bloc de notas si quieres cambiar la escucha. La configuración inicial ya es `0.0.0.0:2323`. Al iniciar el `.exe` con doble clic se abre una ventana de consola que muestra el estado del servidor; déjala abierta mientras uses el emulador. Cierra esa ventana para detenerlo.

El fichero configurable tiene esta forma:

```json
{
  "ListenAddress": "0.0.0.0",
  "Port": 2323
}
```

La escucha `0.0.0.0` permite conexiones desde otras interfaces de red. Puede ser necesario permitir el puerto 2323 en el Firewall de Windows. Telnet **no cifra** el tráfico; úsalo solo en redes confiables. La configuración de switch se guarda separadamente en `data\startup-config.json`, junto al ejecutable.

### Ejecución desde código

También puedes iniciarlo desde PowerShell:

```powershell
dotnet run --project .\SwitchTelnetEmulator
```

Conecta PuTTY en modo **Telnet** a la dirección IP del equipo, puerto `2323` (o `127.0.0.1` desde el mismo equipo). Si está instalado el cliente Telnet de Windows, puedes usar `telnet 127.0.0.1 2323`. Los argumentos `--host`, `--port` y `--config` permiten sobrescribir los valores para esta ejecución.

## Modos y comandos soportados

Los comandos se introducen en inglés, como en IOS. Interfaces disponibles: `FastEthernet0/1`–`FastEthernet0/24` y `GigabitEthernet0/1`–`GigabitEthernet0/2` (también acepta formas abreviadas `Fa0/n`, `Gi0/n`). VLAN 1 viene creada.

```text
enable
configure terminal
hostname LabSwitch
vlan 10
 name Usuarios
exit
interface FastEthernet0/1
 switchport mode access
 switchport access vlan 10
 description Puesto-1
 no shutdown
exit
interface GigabitEthernet0/1
 switchport mode trunk
 switchport trunk allowed vlan 1,10
end
show vlan brief
show interfaces status
show interfaces trunk
show interfaces switchport
show spanning-tree
show running-config
copy running-config startup-config
show startup-config
write memory
```

Otros comandos incluidos: `show version`, `shutdown`, `no shutdown`, `spanning-tree portfast`/`no spanning-tree portfast`, `no vlan <id>`, `exit`, `end`, `disable`, `?`. La CLI soporta abreviaturas seleccionadas (p. ej. `en`, `conf t`, `int`, `sh vlan brief`, `wr mem`); no implementa el algoritmo general de abreviaturas ambiguas de IOS.

`show spanning-tree` es una vista determinista de un switch aislado, no una simulación de topología ni de convergencia. Los puertos representan estado de configuración, no enlaces activos reales. No se incluyen routing, port-channels, seguridad/autenticación, ACL, comandos de configuración seriales, acceso a otros dispositivos o todas las variantes de comandos Packet Tracer.

## Compilar

```powershell
dotnet build .\SwitchTelnetEmulator.sln

dotnet run --project .\SwitchTelnetEmulator.Tests
```
