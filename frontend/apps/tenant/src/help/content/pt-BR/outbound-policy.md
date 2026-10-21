# Controle destinos de saída

## Pré-requisitos

Você precisa de acesso **Gerenciar**. Solicite ao responsável pela rede ou segurança os hosts, portas e CIDRs privados aprovados para tarefas HTTP ou webhooks de alerta. O tenant tem políticas separadas: `tenant-jobs` e `tenant-alerts`.

## Permissão

Usuários com acesso Gerenciar podem atualizar a política de saída. Os destinos são verificados durante a execução; salvar a política não comprova que o serviço remoto está acessível.

## Etapas

1. Abra [Configurações](#/settings) e selecione **Política de saída**. Escolha **tenant-jobs** para tarefas HTTP ou **tenant-alerts** para webhooks de alerta.
2. Informe os hosts aprovados, um por linha. Curingas como `*.example.com` ampliam o acesso e precisam de aprovação. Informe portas como números separados por vírgula, por exemplo `443,8443`; a página aceita até 20 hosts e 32 portas.
3. Adicione CIDRs privados somente para uma necessidade analisada. Destinos privados, de loopback, link-local e outros inseguros são bloqueados por padrão. Ative **Permitir HTTP** somente para um destino sem TLS aprovado; prefira HTTPS.
4. Escolha **Salvar**. Se outra pessoa alterou a política, recarregue a versão mais recente e revise-a antes de salvar.

## Exemplo

Para um receptor aprovado em `hooks.example.net` por HTTPS, selecione `tenant-alerts`, adicione o host, permita a porta `443`, mantenha **Permitir HTTP** desativado e salve. Atualize `tenant-jobs` separadamente se uma tarefa HTTP também precisar desse host.

## Verificação

Recarregue a política e confirme host, porta e configuração HTTP. Depois, faça uma solicitação de teste controlada e consulte o código de resultado seguro. DNS e conectividade são verificados durante a execução.

## Recuperação

Se o destino for bloqueado, confira a política correta, hostname exato, porta efetiva e destino de redirecionamento. Adicione somente o destino analisado necessário. Não contorne o bloqueio com grandes faixas privadas ou HTTP sem aprovação.

## Guias relacionados

Consulte [destinos de webhook](#/help:webhook-sinks), [tarefas HTTP](#/help:http-jobs), [segredos](#/help:secrets) e [configurações do tenant](#/help:settings).
